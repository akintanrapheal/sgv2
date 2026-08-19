using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

/// <summary>
/// Step 2 of the refund workflow: once Finance approves a restock-requested refund, its returned units
/// land here for Inventory to decide per item — how many to put back on the shelf, and (for the rest) a
/// reason for keeping them off (Damaged / Wrong item sold / …). No money is involved; the payout already
/// happened at Finance approval. Also shows a history of what was already resolved.
/// </summary>
public class ReturnsController : InventoryAreaController
{
    private readonly ApplicationDbContext _db;
    private readonly IRefundApprovalService _refunds;

    // Reasons offered when returned units are NOT put back on the shelf.
    public static readonly string[] WriteOffReasons =
        { "Damaged", "Wrong item sold", "Expired / quality issue", "Customer changed mind", "Other" };

    public ReturnsController(ApplicationDbContext db, IRefundApprovalService refunds)
    {
        _db = db;
        _refunds = refunds;
    }

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Returns to restock";

        var pending = await _db.Refunds
            .Where(r => r.Status == RefundStatus.Approved && r.RestockRequested
                     && r.Items.Any(i => i.RestockDecision == RestockDecision.Pending))
            .OrderBy(r => r.DecisionAt)
            .Select(r => new PendingReturnVm
            {
                Id = r.Id,
                Number = r.RefundNumber,
                Order = r.OriginalOrder.OrderNumber,
                OrderId = r.OriginalOrderId,
                Channel = r.OriginalOrder.Channel.ToString(),
                ApprovedAt = r.DecisionAt,
                StoreName = _db.Stores.Where(s => s.Id == r.RestockStoreId).Select(s => s.Name).FirstOrDefault() ?? "—",
                Reason = string.IsNullOrWhiteSpace(r.Reason) ? "—" : r.Reason!,
                Items = r.Items.Where(i => i.RestockDecision == RestockDecision.Pending)
                    .Select(i => new PendingReturnItem { ItemId = i.Id, Name = i.ProductName, Variant = i.VariantName, Qty = i.Quantity })
                    .ToList()
            })
            .ToListAsync();

        // History — refunds whose returned units have already been resolved (restocked and/or written off).
        var resolvedRaw = await _db.Refunds
            .Where(r => r.Status == RefundStatus.Approved && r.RestockRequested
                     && r.Items.Any(i => i.RestockDecision != RestockDecision.Pending))
            .OrderByDescending(r => r.Items.Max(i => i.RestockDecidedAt))
            .Take(50)
            .Select(r => new
            {
                r.Id, r.RefundNumber, Order = r.OriginalOrder.OrderNumber, OrderId = r.OriginalOrderId,
                StoreName = _db.Stores.Where(s => s.Id == r.RestockStoreId).Select(s => s.Name).FirstOrDefault() ?? "—",
                DecidedAt = r.Items.Max(i => i.RestockDecidedAt),
                DeciderId = r.Items.Where(i => i.RestockDecidedByUserId != null).Select(i => i.RestockDecidedByUserId).FirstOrDefault(),
                Items = r.Items.Where(i => i.RestockDecision != RestockDecision.Pending)
                    .Select(i => new ResolvedItem { Name = i.ProductName, Variant = i.VariantName, Qty = i.Quantity, Restocked = i.RestockedQuantity, Reason = i.RestockNote })
                    .ToList()
            })
            .ToListAsync();
        var deciderIds = resolvedRaw.Where(r => r.DeciderId != null).Select(r => r.DeciderId!).Distinct().ToList();
        var names = (await _db.Users.Where(u => deciderIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FirstName, u.LastName, u.UserName }).ToListAsync())
            .ToDictionary(u => u.Id, u => { var n = $"{u.FirstName} {u.LastName}".Trim(); return string.IsNullOrWhiteSpace(n) ? (u.UserName ?? "—") : n; });
        var resolved = resolvedRaw.Select(r => new ResolvedReturnVm
        {
            Id = r.Id, Number = r.RefundNumber, Order = r.Order, OrderId = r.OrderId, StoreName = r.StoreName,
            DecidedAt = r.DecidedAt, DecidedBy = r.DeciderId != null ? names.GetValueOrDefault(r.DeciderId, "—") : "—",
            Items = r.Items
        }).ToList();

        ViewBag.Reasons = WriteOffReasons;
        return View(new ReturnsPageVm { Pending = pending, Resolved = resolved });
    }

    // One return in full — the order, WHY Finance approved it, and the per-item restock decision.
    public async Task<IActionResult> Detail(int id)
    {
        var r = await _db.Refunds
            .Include(x => x.Items)
            .Include(x => x.OriginalOrder).ThenInclude(o => o.Customer)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (r == null) return NotFound();
        if (!r.RestockRequested) { TempData["Error"] = "This refund has no items to return to stock."; return RedirectToAction(nameof(Index)); }
        ViewData["Title"] = $"Return {r.RefundNumber}";
        var o = r.OriginalOrder;

        var pids = r.Items.Select(i => i.ProductId).Distinct().ToList();
        var images = (await _db.ProductImages.Where(im => pids.Contains(im.ProductId))
                .Select(im => new { im.ProductId, im.Url, im.IsPrimary, im.SortOrder }).ToListAsync())
            .GroupBy(im => im.ProductId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.IsPrimary).ThenBy(x => x.SortOrder).Select(x => x.Url).FirstOrDefault());

        var ids = new[] { r.CashierUserId, r.ApprovedByUserId }.Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).Distinct().ToList();
        var names = (await _db.Users.Where(u => ids.Contains(u.Id))
                .Select(u => new { u.Id, u.FirstName, u.LastName, u.UserName }).ToListAsync())
            .ToDictionary(u => u.Id, u => { var n = $"{u.FirstName} {u.LastName}".Trim(); return string.IsNullOrWhiteSpace(n) ? (u.UserName ?? "—") : n; });

        var cust = o.Customer;
        var custName = cust != null ? $"{cust.FirstName} {cust.LastName}".Trim() : "";

        ViewBag.Reasons = WriteOffReasons;
        return View(new ReturnDetailVm
        {
            Id = r.Id, Number = r.RefundNumber, Order = o.OrderNumber, OrderId = o.Id,
            Channel = o.Channel.ToString(),
            StoreName = await _db.Stores.Where(s => s.Id == r.RestockStoreId).Select(s => s.Name).FirstOrDefaultAsync() ?? "—",
            ApprovedAt = r.DecisionAt,
            RefundAmount = r.Amount, RefundMethod = r.RefundMethod,
            RefundReason = string.IsNullOrWhiteSpace(r.Reason) ? "—" : r.Reason!,
            FinanceNote = r.DecisionNote,
            RequestedBy = names.GetValueOrDefault(r.CashierUserId ?? "", "—"),
            ApprovedBy = string.IsNullOrEmpty(r.ApprovedByUserId) ? "—" : names.GetValueOrDefault(r.ApprovedByUserId!, "—"),
            CustomerName = string.IsNullOrWhiteSpace(custName) ? "Walk-in" : custName,
            CustomerPhone = cust?.PhoneNumber, CustomerEmail = cust?.Email,
            OrderTotal = o.Total, OrderDate = o.CreatedAt,
            AnyPending = r.Items.Any(i => i.RestockDecision == RestockDecision.Pending),
            Items = r.Items.OrderBy(i => i.ProductName).Select(i => new ReturnDetailItem
            {
                ItemId = i.Id, Name = i.ProductName, Variant = i.VariantName, Qty = i.Quantity,
                Image = images.GetValueOrDefault(i.ProductId),
                Pending = i.RestockDecision == RestockDecision.Pending,
                Restocked = i.RestockedQuantity, WrittenOff = i.Quantity - i.RestockedQuantity,
                Note = i.RestockNote, UnitPrice = i.UnitPrice
            }).ToList()
        });
    }

    // restockQty[<itemId>] = how many to put back; reason[<itemId>] = why the rest are not restocked.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Resolve(int id, [FromForm] Dictionary<int, int> restockQty, [FromForm] Dictionary<int, string> reason)
    {
        var decisions = (restockQty ?? new()).Select(kv =>
            new RestockLineDecision(kv.Key, kv.Value, (reason != null && reason.TryGetValue(kv.Key, out var rz)) ? rz : null)).ToList();

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var res = await _refunds.ResolveStockAsync(id, decisions, userId);
        if (res.Success)
            await LogAsync("RefundRestock", "Refund", id.ToString(), res.Message);
        TempData[res.Success ? "Success" : "Error"] = res.Message;
        return RedirectToAction(nameof(Index));
    }
}

public class ReturnsPageVm
{
    public List<PendingReturnVm> Pending { get; set; } = new();
    public List<ResolvedReturnVm> Resolved { get; set; } = new();
}

public class PendingReturnVm
{
    public int Id { get; set; }
    public string Number { get; set; } = "";
    public string Order { get; set; } = "";
    public int OrderId { get; set; }
    public string Channel { get; set; } = "";
    public DateTime? ApprovedAt { get; set; }
    public string StoreName { get; set; } = "";
    public string Reason { get; set; } = "";
    public List<PendingReturnItem> Items { get; set; } = new();
}

public class PendingReturnItem
{
    public int ItemId { get; set; }
    public string Name { get; set; } = "";
    public string? Variant { get; set; }
    public int Qty { get; set; }
}

public class ResolvedReturnVm
{
    public int Id { get; set; }
    public string Number { get; set; } = "";
    public string Order { get; set; } = "";
    public int OrderId { get; set; }
    public string StoreName { get; set; } = "";
    public DateTime? DecidedAt { get; set; }
    public string DecidedBy { get; set; } = "—";
    public List<ResolvedItem> Items { get; set; } = new();
}

public class ResolvedItem
{
    public string Name { get; set; } = "";
    public string? Variant { get; set; }
    public int Qty { get; set; }
    public int Restocked { get; set; }
    public int WrittenOff => Qty - Restocked;
    public string? Reason { get; set; }
}

public class ReturnDetailVm
{
    public int Id { get; set; }
    public string Number { get; set; } = "";
    public string Order { get; set; } = "";
    public int OrderId { get; set; }
    public string Channel { get; set; } = "";
    public string StoreName { get; set; } = "";
    public DateTime? ApprovedAt { get; set; }
    public decimal RefundAmount { get; set; }
    public string RefundMethod { get; set; } = "";
    public string RefundReason { get; set; } = "";
    public string? FinanceNote { get; set; }
    public string RequestedBy { get; set; } = "—";
    public string ApprovedBy { get; set; } = "—";
    public string CustomerName { get; set; } = "Walk-in";
    public string? CustomerPhone { get; set; }
    public string? CustomerEmail { get; set; }
    public decimal OrderTotal { get; set; }
    public DateTime OrderDate { get; set; }
    public bool AnyPending { get; set; }
    public List<ReturnDetailItem> Items { get; set; } = new();
}

public class ReturnDetailItem
{
    public int ItemId { get; set; }
    public string Name { get; set; } = "";
    public string? Variant { get; set; }
    public int Qty { get; set; }
    public decimal UnitPrice { get; set; }
    public string? Image { get; set; }
    public bool Pending { get; set; }
    public int Restocked { get; set; }
    public int WrittenOff { get; set; }
    public string? Note { get; set; }
}
