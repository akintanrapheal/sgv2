using System.Security.Claims;
using System.Text;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

public class TransfersController : InventoryAreaController
{
    private readonly ApplicationDbContext _db;
    private readonly ITransferWorkflowService _workflow;
    private readonly IStoreAccessService _access;
    private readonly IManifestTokenService _manifestTokens;

    public TransfersController(ApplicationDbContext db, ITransferWorkflowService workflow, IStoreAccessService access, IManifestTokenService manifestTokens)
    {
        _db = db;
        _workflow = workflow;
        _access = access;
        _manifestTokens = manifestTokens;
    }

    // Store-level authorization (writes-only): a non-admin may only act on a transfer that
    // touches a branch they're assigned to. `from`/`to` pick which side(s) the action affects.
    // Returns an error result when denied, otherwise null.
    private async Task<IActionResult?> DenyIfNoStoreAccessAsync(int id, bool from, bool to)
    {
        var t = await _db.StockTransfers.Where(x => x.Id == id)
            .Select(x => new { x.FromStoreId, x.ToStoreId }).FirstOrDefaultAsync();
        if (t == null) return null; // let the workflow report "not found"
        var writable = await _access.WritableStoreIdsAsync(User);
        var ok = (from && writable.Contains(t.FromStoreId)) || (to && writable.Contains(t.ToStoreId));
        return ok ? null : Json(new { success = false, message = "You don't have access to this transfer's branch." });
    }

    public async Task<IActionResult> Index(int? storeId, string status = "all", string q = "", int page = 1)
    {
        const int pageSize = 40;
        ViewData["Title"] = "Transfers";
        ViewBag.Stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        ViewBag.StoreId = storeId;
        ViewBag.Status = status;
        ViewBag.Query = q ?? "";

        var baseQuery = _db.StockTransfers.AsQueryable();
        if (storeId.HasValue)
            baseQuery = baseQuery.Where(t => t.FromStoreId == storeId.Value || t.ToStoreId == storeId.Value);

        var counts = await baseQuery.GroupBy(t => t.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync();
        ViewBag.StatusCounts = counts.ToDictionary(c => c.Status, c => c.Count);
        ViewBag.TotalCount = counts.Sum(c => c.Count);

        IQueryable<StockTransfer> query = ApplySearch(ApplyStatusFilter(
            baseQuery.Include(t => t.FromStore).Include(t => t.ToStore).Include(t => t.Items), status), q);

        if (page < 1) page = 1;
        var filteredCount = await query.CountAsync();
        ViewBag.Page = page;
        ViewBag.TotalPages = (int)Math.Ceiling(filteredCount / (double)pageSize);
        var transfers = await query.OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        return View(transfers);
    }

    // Free-text search over transfer number, either branch name, or a line item's product — shared by the
    // list and both exports so they always agree.
    private static IQueryable<StockTransfer> ApplySearch(IQueryable<StockTransfer> query, string? q)
    {
        q = (q ?? "").Trim();
        if (q.Length == 0) return query;
        var like = $"%{q}%";
        return query.Where(t => EF.Functions.ILike(t.TransferNumber, like)
            || EF.Functions.ILike(t.FromStore.Name, like)
            || EF.Functions.ILike(t.ToStore.Name, like)
            || t.Items.Any(i => EF.Functions.ILike(i.ProductName, like)));
    }

    // Shared status → query filter, used by the list and both exports so one source of truth.
    private static IQueryable<StockTransfer> ApplyStatusFilter(IQueryable<StockTransfer> query, string status) => status switch
    {
        "pending" => query.Where(t => t.Status == TransferStatus.PendingApproval),
        "approved" => query.Where(t => t.Status == TransferStatus.Approved),
        "intransit" => query.Where(t => t.Status == TransferStatus.InTransit),
        "partial" => query.Where(t => t.Status == TransferStatus.PartiallyReceived),
        "completed" => query.Where(t => t.Status == TransferStatus.Completed),
        "rejected" => query.Where(t => t.Status == TransferStatus.Rejected),
        "cancelled" => query.Where(t => t.Status == TransferStatus.Cancelled),
        _ => query
    };

    // Column headers for the detailed (one row per transferred item) export.
    private static readonly string[] ExportHeaders =
    {
        "Transfer #", "Status", "From", "To", "Created", "Created by",
        "Approved", "Approved by", "Dispatched", "Dispatched by", "Tracking", "Courier",
        "Received", "Received by", "Cancelled/Rejected", "Cancelled/Rejected by", "Reason",
        "Note", "Online order", "Product SKU", "Product", "Variant",
        "Requested", "Approved qty", "Dispatched qty", "Received", "Damaged", "Won't fulfil", "Pending"
    };

    /// <summary>Builds the flat, one-row-per-item dataset for the export (respects the branch + status
    /// filters, all pages). Cells are typed (DateTimes already in local/WAT time, quantities as ints)
    /// so the Excel writer can format them and the CSV writer can stringify consistently.</summary>
    private async Task<List<object?[]>> BuildExportRowsAsync(int? storeId, string status, string q = "")
    {
        IQueryable<StockTransfer> query = _db.StockTransfers
            .Include(t => t.FromStore).Include(t => t.ToStore).Include(t => t.Items);
        if (storeId.HasValue)
            query = query.Where(t => t.FromStoreId == storeId.Value || t.ToStoreId == storeId.Value);
        var transfers = await ApplySearch(ApplyStatusFilter(query, status), q)
            .OrderByDescending(t => t.CreatedAt).ToListAsync();

        // Resolve people's names and product SKUs / online-order numbers in one round-trip each.
        var userIds = transfers.SelectMany(t => new[]
            {
                t.CreatedByUserId, t.ApprovedByUserId, t.RejectedByUserId,
                t.DispatchedByUserId, t.ReceivedByUserId, t.CancelledByUserId
            })
            .Where(u => u != null).Cast<string>().Distinct().ToList();
        var names = (await _db.Users.Where(u => userIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email, u.UserName }).ToListAsync())
            .ToDictionary(u => u.Id, u =>
            {
                var n = $"{u.FirstName} {u.LastName}".Trim();
                return !string.IsNullOrWhiteSpace(n) ? n : (u.Email ?? u.UserName ?? u.Id);
            });
        string Name(string? id) => id != null && names.TryGetValue(id, out var n) ? n : "";

        var productIds = transfers.SelectMany(t => t.Items).Select(i => i.ProductId).Distinct().ToList();
        var skus = await _db.Products.Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Sku ?? "");

        var orderIds = transfers.Where(t => t.OrderId.HasValue).Select(t => t.OrderId!.Value).Distinct().ToList();
        var orderNumbers = await _db.Orders.Where(o => orderIds.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, o => o.OrderNumber);

        DateTime? Local(DateTime? d) => d?.ToLocalTime();

        var rows = new List<object?[]>();
        foreach (var t in transfers)
        {
            var reason = !string.IsNullOrWhiteSpace(t.CancellationReason) ? t.CancellationReason
                       : t.RejectionReason ?? "";
            var whoEnded = t.Status == TransferStatus.Cancelled ? Name(t.CancelledByUserId)
                         : t.Status == TransferStatus.Rejected ? Name(t.RejectedByUserId) : "";
            var whenEnded = t.Status == TransferStatus.Cancelled ? Local(t.CancelledAt)
                          : t.Status == TransferStatus.Rejected ? Local(t.RejectedAt) : null;
            var orderNo = t.OrderId.HasValue && orderNumbers.TryGetValue(t.OrderId.Value, out var on) ? on : "";

            // Header fields repeated on each item line so the sheet filters/pivots cleanly.
            object?[] Head() => new object?[]
            {
                t.TransferNumber, TransferStatusDisplay.Label(t.Status), t.FromStore?.Name, t.ToStore?.Name,
                Local(t.CreatedAt), Name(t.CreatedByUserId),
                Local(t.ApprovedAt), Name(t.ApprovedByUserId),
                Local(t.DispatchedAt), Name(t.DispatchedByUserId), t.TrackingNumber ?? "", t.CourierName ?? "",
                Local(t.ReceivedAt), Name(t.ReceivedByUserId),
                whenEnded, whoEnded, reason, t.Note ?? "", orderNo
            };

            if (t.Items.Count == 0)
            {
                rows.Add(Head().Concat(new object?[] { "", "", "", 0, null, null, null, null, null, 0 }).ToArray());
                continue;
            }
            foreach (var i in t.Items)
            {
                var sku = skus.TryGetValue(i.ProductId, out var s) ? s : "";
                rows.Add(Head().Concat(new object?[]
                {
                    sku, i.ProductName, i.VariantName ?? "",
                    i.RequestedQty, i.ApprovedQty, i.DispatchedQty,
                    i.ReceivedQty, i.DamagedQty, i.WontFulfilQty, i.PendingQty
                }).ToArray());
            }
        }
        return rows;
    }

    private static string ExportBaseName(string status)
    {
        var scope = status == "all" ? "" : "-" + status;
        return $"stock-transfers{scope}_{DateTime.UtcNow.ToLocalTime():yyyyMMdd-HHmm}";
    }

    // Detailed CSV export (one row per transferred item), honouring the current branch + status + search.
    public async Task<IActionResult> ExportCsv(int? storeId, string status = "all", string q = "")
    {
        var rows = await BuildExportRowsAsync(storeId, status, q);
        var sb = new StringBuilder();
        Csv.AppendRow(sb, ExportHeaders);
        foreach (var r in rows)
            Csv.AppendRow(sb, r.Select(CsvCell).ToArray());
        return File(Csv.ToBytes(sb), "text/csv", ExportBaseName(status) + ".csv");

        static string CsvCell(object? v) => v switch
        {
            null => "",
            DateTime d => d.ToString("yyyy-MM-dd HH:mm"),
            _ => v.ToString() ?? ""
        };
    }

    // Detailed Excel (.xlsx) export — same data as the CSV, with typed cells, a frozen bold header row,
    // date formatting and an auto-filter so staff can sort/pivot.
    public async Task<IActionResult> ExportExcel(int? storeId, string status = "all", string q = "")
    {
        var rows = await BuildExportRowsAsync(storeId, status, q);

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Transfers");

        for (var c = 0; c < ExportHeaders.Length; c++)
            ws.Cell(1, c + 1).Value = ExportHeaders[c];
        var header = ws.Row(1);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#F3F4F6");

        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            for (var c = 0; c < row.Length; c++)
            {
                var cell = ws.Cell(r + 2, c + 1);
                switch (row[c])
                {
                    case null: break;
                    case DateTime d:
                        cell.Value = d;
                        cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
                        break;
                    case int n: cell.Value = n; break;
                    default: cell.Value = row[c]!.ToString(); break;
                }
            }
        }

        ws.SheetView.FreezeRows(1);
        ws.RangeUsed()?.SetAutoFilter();
        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return File(ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ExportBaseName(status) + ".xlsx");
    }

    public async Task<IActionResult> Create()
    {
        ViewData["Title"] = "Request Transfer";
        ViewBag.Stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        return View();
    }

    // Search products with stock at the source branch (JSON).
    [HttpGet]
    public async Task<IActionResult> SearchStock(string? q, int fromStoreId)
    {
        q = (q ?? "").Trim();
        var query = _db.Products.Where(p => p.IsActive);
        if (q.Length > 0)
            query = query.Where(p => EF.Functions.ILike(p.Name, $"%{q}%")
                                  || EF.Functions.ILike(p.Sku ?? "", $"%{q}%")
                                  || EF.Functions.ILike(p.Barcode ?? "", $"%{q}%")
                                  || p.Variants.Any(v => EF.Functions.ILike(v.Barcode ?? "", $"%{q}%")));

        var raw = await query.OrderBy(p => p.Name).Take(40)
            .Select(p => new
            {
                id = p.Id,
                name = p.Name,
                sku = p.Sku,
                barcode = p.Barcode,
                // Product-level stock (for products with no variants) lives on the null-variant row.
                productStock = p.StoreInventories.Where(si => si.StoreId == fromStoreId && si.ProductVariantId == null)
                    .Select(si => si.QuantityOnHand).FirstOrDefault(),
                variants = p.Variants.Select(v => new
                {
                    id = v.Id,
                    name = v.Name,
                    barcode = v.Barcode,
                    stock = p.StoreInventories.Where(si => si.StoreId == fromStoreId && si.ProductVariantId == v.Id)
                        .Select(si => si.QuantityOnHand).FirstOrDefault()
                }).ToList()
            })
            .ToListAsync();

        // A variant product's stock lives on its variant rows — surface each in-stock variant so the
        // UI can prompt for which one. A plain product uses its own (null-variant) stock.
        var products = raw.Select(p => new
        {
            p.id, p.name, p.sku, p.barcode,
            hasVariants = p.variants.Count > 0,
            stock = p.variants.Count > 0 ? p.variants.Sum(v => v.stock) : p.productStock,
            variants = p.variants.Where(v => v.stock > 0).OrderBy(v => v.name)
                .Select(v => new { v.id, v.name, v.barcode, v.stock }).ToList()
        }).Where(p => p.stock > 0).ToList();
        return Json(products);
    }

    // ActionName keeps the public route /Inventory/Transfers/Request; the method is renamed so it
    // doesn't hide ControllerBase.Request (the HttpRequest) — was a CS0108 build warning.
    [HttpPost, ActionName("Request"), ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestTransfer([FromBody] TransferRequest req)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var writable = await _access.WritableStoreIdsAsync(User);
        if (!writable.Contains(req.FromStoreId) && !writable.Contains(req.ToStoreId))
            return Json(new { success = false, message = "You can only request transfers involving your assigned branch(es)." });
        var (success, error, id) = await _workflow.RequestAsync(req, userId);
        if (!success) return Json(new { success = false, message = error });

        var transfer = await _db.StockTransfers
            .Include(t => t.FromStore).Include(t => t.ToStore).Include(t => t.Items)
            .FirstAsync(t => t.Id == id);
        await LogAsync("Create", "Transfer", id.ToString(),
            $"Transfer {transfer.TransferNumber} requested: {transfer.FromStore.Name} → {transfer.ToStore.Name} ({transfer.Items.Count} line(s))");

        return Json(new { success = true, id });
    }

    public class ItemsRequest { public List<ItemQtyDto> Items { get; set; } = new(); }
    public class ReasonRequest { public string Reason { get; set; } = ""; }
    public class DispatchRequest
    {
        public List<ItemQtyDto> Items { get; set; } = new();
        public string? TrackingNumber { get; set; }
        public string? CourierName { get; set; }
        public string? Notes { get; set; }
    }
    public class ReceiveLine
    {
        public int ItemId { get; set; }
        public int Received { get; set; }
        public int Damaged { get; set; }
        public int WontFulfil { get; set; }
    }
    public class ReceiveRequest
    {
        public List<ReceiveLine> Items { get; set; } = new();
        public string? Notes { get; set; }
    }

    // Approving/rejecting a transfer is an administrator decision only — inventory staff can create
    // and request transfers, but a full admin signs off before stock moves.
    private const string AdminRole = "Admin";

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id, [FromBody] ItemsRequest req)
    {
        if (!SterlingLams.Web.Areas.Admin.AdminSections.IsFullAccess(User)) return Json(new { success = false, message = "Only an administrator can approve transfers." });
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var deny = await DenyIfNoStoreAccessAsync(id, from: true, to: false); if (deny != null) return deny;
        var result = await _workflow.ApproveAsync(id, req.Items, userId);
        if (!result.Success) return Json(new { success = false, message = result.Error });
        await LogTransferAsync(id, "Approve", "Approved");
        return Json(new { success = true });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(int id, [FromBody] ReasonRequest req)
    {
        if (!SterlingLams.Web.Areas.Admin.AdminSections.IsFullAccess(User)) return Json(new { success = false, message = "Only an administrator can reject transfers." });
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var deny = await DenyIfNoStoreAccessAsync(id, from: true, to: false); if (deny != null) return deny;
        var result = await _workflow.RejectAsync(id, req.Reason, userId);
        if (!result.Success) return Json(new { success = false, message = result.Error });
        await LogTransferAsync(id, "Reject", $"Rejected ({req.Reason})");
        return Json(new { success = true });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Dispatch(int id, [FromBody] DispatchRequest req)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var deny = await DenyIfNoStoreAccessAsync(id, from: true, to: false); if (deny != null) return deny;
        var result = await _workflow.DispatchAsync(id, req.Items, req.TrackingNumber, req.CourierName, req.Notes, userId);
        if (!result.Success) return Json(new { success = false, message = result.Error });
        await LogTransferAsync(id, "Dispatch", "Dispatched");
        return Json(new { success = true });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Receive(int id, [FromBody] ReceiveRequest req)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var deny = await DenyIfNoStoreAccessAsync(id, from: false, to: true); if (deny != null) return deny;
        var lines = req.Items.Select(i => new ReceiveLineDto(i.ItemId, i.Received, i.Damaged, i.WontFulfil)).ToList();
        var result = await _workflow.ReceiveAsync(id, lines, req.Notes, userId);
        if (!result.Success) return Json(new { success = false, message = result.Error });
        await LogTransferAsync(id, "Receive", "Received");
        return Json(new { success = true });
    }

    public class CompleteRequest { public string Disposition { get; set; } = ""; public string Reason { get; set; } = ""; }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Complete(int id, [FromBody] CompleteRequest req)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var deny = await DenyIfNoStoreAccessAsync(id, from: false, to: true); if (deny != null) return deny;
        var result = await _workflow.CompleteAsync(id, req?.Disposition ?? "", req?.Reason ?? "", userId);
        if (!result.Success) return Json(new { success = false, message = result.Error });
        var how = string.Equals(req?.Disposition, "return", StringComparison.OrdinalIgnoreCase) ? "returned to origin" : "written off";
        await LogTransferAsync(id, "Complete", $"Closed — pending stock {how} ({req?.Reason?.Trim()})");
        return Json(new { success = true });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, [FromBody] ReasonRequest req)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var deny = await DenyIfNoStoreAccessAsync(id, from: true, to: true); if (deny != null) return deny;
        var result = await _workflow.CancelAsync(id, req.Reason, userId);
        if (!result.Success) return Json(new { success = false, message = result.Error });
        await LogTransferAsync(id, "Cancel", $"Cancelled ({req.Reason})");
        return Json(new { success = true });
    }

    private async Task LogTransferAsync(int id, string action, string description)
    {
        var tn = await _db.StockTransfers.Where(t => t.Id == id).Select(t => t.TransferNumber).FirstOrDefaultAsync();
        await LogAsync(action, "Transfer", id.ToString(), $"Transfer {tn}: {description}");
    }

    public async Task<IActionResult> Detail(int id)
    {
        var transfer = await _db.StockTransfers
            .Include(t => t.FromStore).Include(t => t.ToStore).Include(t => t.Items)
            .FirstOrDefaultAsync(t => t.Id == id);
        if (transfer == null) return NotFound();
        ViewData["Title"] = transfer.TransferNumber;

        var userIds = new[]
            {
                transfer.CreatedByUserId, transfer.ApprovedByUserId, transfer.RejectedByUserId,
                transfer.DispatchedByUserId, transfer.ReceivedByUserId, transfer.CancelledByUserId
            }
            .Where(uid => uid != null).Cast<string>().Distinct().ToList();
        // Show the person's NAME (e.g. a cashier who requested the transfer), not their raw "cashier-<id>"
        // username/email. Falls back to email then username only when there's no name on the account.
        ViewBag.UserNames = (await _db.Users.Where(u => userIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email, u.UserName }).ToListAsync())
            .ToDictionary(u => u.Id, u =>
            {
                var name = $"{u.FirstName} {u.LastName}".Trim();
                return !string.IsNullOrWhiteSpace(name) ? name : (u.Email ?? u.UserName ?? u.Id);
            });

        var productIds = transfer.Items.Select(i => i.ProductId).Distinct().ToList();
        // A product can have several StoreInventory rows at one store (the null-variant pool + each
        // variant row), so sum available per product rather than ToDictionary on ProductId (which
        // would throw on the duplicate key).
        var invRows = await _db.StoreInventories
            .Where(si => si.StoreId == transfer.FromStoreId && productIds.Contains(si.ProductId))
            .Select(si => new { si.ProductId, si.QuantityOnHand, si.QuantityReserved })
            .ToListAsync();
        ViewBag.AvailableQty = invRows
            .GroupBy(x => x.ProductId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.QuantityOnHand - x.QuantityReserved));

        // Primary image per product for the line thumbnails.
        ViewBag.ProductImages = (await _db.ProductImages.Where(im => productIds.Contains(im.ProductId))
                .GroupBy(im => im.ProductId)
                .Select(g => new { Pid = g.Key, Url = g.OrderByDescending(x => x.IsPrimary).Select(x => x.Url).FirstOrDefault() })
                .ToListAsync())
            .Where(x => !string.IsNullOrEmpty(x.Url))
            .ToDictionary(x => x.Pid, x => x.Url!);

        return View(transfer);
    }

    // Printable transfer receipt — minimal standalone page (no app chrome) that auto-prints.
    public async Task<IActionResult> Receipt(int id)
    {
        var transfer = await _db.StockTransfers
            .Include(t => t.FromStore).Include(t => t.ToStore).Include(t => t.Items)
            .FirstOrDefaultAsync(t => t.Id == id);
        if (transfer == null) return NotFound();

        var userIds = new[] { transfer.CreatedByUserId, transfer.DispatchedByUserId, transfer.ReceivedByUserId }
            .Where(uid => uid != null).Cast<string>().Distinct().ToList();
        // Show the person's NAME (e.g. a cashier who requested the transfer), not their raw "cashier-<id>"
        // username/email. Falls back to email then username only when there's no name on the account.
        ViewBag.UserNames = (await _db.Users.Where(u => userIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email, u.UserName }).ToListAsync())
            .ToDictionary(u => u.Id, u =>
            {
                var name = $"{u.FirstName} {u.LastName}".Trim();
                return !string.IsNullOrWhiteSpace(name) ? name : (u.Email ?? u.UserName ?? u.Id);
            });
        ViewData["Title"] = $"Receipt {transfer.TransferNumber}";
        return View(transfer);
    }

    /// <summary>Sends staff to the public tokenised manifest (one render path, and the same link a
    /// receiver without an account can open). Only meaningful once the transfer is approved.</summary>
    public async Task<IActionResult> Manifest(int id)
    {
        var approvedAt = await _db.StockTransfers.Where(t => t.Id == id)
            .Select(t => (DateTime?)t.ApprovedAt).FirstOrDefaultAsync();
        if (approvedAt == null)
        {
            TempData["Error"] = "The manifest is available once the transfer is approved.";
            return RedirectToAction(nameof(Detail), new { id });
        }
        return Redirect($"/t/manifest/{_manifestTokens.Protect(id)}");
    }
}
