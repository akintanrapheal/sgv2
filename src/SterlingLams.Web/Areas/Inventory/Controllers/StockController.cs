using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

public class StockController : InventoryAreaController
{
    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;
    private readonly IStoreAccessService _access;
    private const int PageSize = 30;

    public StockController(ApplicationDbContext db, IStockService stock, IStoreAccessService access)
    {
        _db = db;
        _stock = stock;
        _access = access;
    }

    // Stock Management — EPOS-style single-location grid: Sale price, Stock, On order, Min, Max for
    // the selected branch. Variant products show read-only roll-up + editable per-variant rows.
    public async Task<IActionResult> Index(string q = "", int page = 1, int? categoryId = null, int? storeId = null, int pageSize = 50)
    {
        ViewData["Title"] = "Stock Management";
        if (pageSize != 25 && pageSize != 50 && pageSize != 100) pageSize = 50;

        var stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        if (stores.Count == 0)
            return View(new StockManagementVm { Categories = await _db.Categories.OrderBy(c => c.Name).ToListAsync() });

        // Default to the user's first writable branch, else the first active branch.
        var writable = await _access.WritableStoreIdsAsync(User);
        var selStore = storeId ?? stores.FirstOrDefault(s => writable.Contains(s.Id))?.Id ?? stores[0].Id;
        if (stores.All(s => s.Id != selStore)) selStore = stores[0].Id;

        var pq = _db.Products.Include(p => p.Category).Include(p => p.Variants.Where(v => v.IsActive))
            .Where(p => p.IsActive).AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
            pq = pq.Where(p => EF.Functions.ILike(p.Name, $"%{q}%")
                            || EF.Functions.ILike(p.Sku ?? "", $"%{q}%")
                            || EF.Functions.ILike(p.Barcode ?? "", $"%{q}%")
                            || p.Variants.Any(v => EF.Functions.ILike(v.Barcode ?? "", $"%{q}%")));
        if (categoryId.HasValue)
            pq = pq.Where(p => p.CategoryId == categoryId.Value);

        var total = await pq.CountAsync();
        var prods = await pq.OrderBy(p => p.Name).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        var pids = prods.Select(p => p.Id).ToList();
        var inv = pids.Count > 0
            ? await _db.StoreInventories.Where(si => pids.Contains(si.ProductId) && si.StoreId == selStore).ToListAsync()
            : new List<StoreInventory>();

        var rows = prods.Select(p =>
        {
            var pool = inv.FirstOrDefault(si => si.ProductId == p.Id && si.ProductVariantId == null);
            return new StockMgmtRow
            {
                ProductId = p.Id,
                Name = p.Name,
                Sku = p.Sku,
                Category = p.Category?.Name ?? "—",
                Price = p.Price,
                OnHand = pool?.QuantityOnHand ?? 0,
                OnOrder = pool?.OnOrder ?? 0,
                Min = pool?.MinStock,
                Max = pool?.MaxStock,
                Variants = p.Variants.Where(v => v.IsActive).OrderBy(v => v.Name).Select(v =>
                {
                    var vr = inv.FirstOrDefault(si => si.ProductId == p.Id && si.ProductVariantId == v.Id);
                    return new StockMgmtVariantRow
                    {
                        VariantId = v.Id, Name = v.Name, Sku = v.Sku,
                        OnHand = vr?.QuantityOnHand ?? 0, OnOrder = vr?.OnOrder ?? 0,
                        Min = vr?.MinStock, Max = vr?.MaxStock
                    };
                }).ToList()
            };
        }).ToList();

        return View(new StockManagementVm
        {
            Stores = stores,
            SelectedStoreId = selStore,
            Rows = rows,
            Query = q,
            CategoryFilter = categoryId,
            Categories = await _db.Categories.OrderBy(c => c.Name).ToListAsync(),
            Page = page,
            PageSize = pageSize,
            TotalPages = (int)Math.Ceiling(total / (double)pageSize),
            Total = total,
            FirstRow = total == 0 ? 0 : (page - 1) * pageSize + 1,
            LastRow = Math.Min(page * pageSize, total)
        });
    }

    // Full-page per-location Inventory editor (Stock Management row kebab → "Inventory").
    // Reuses Products.TrackStockData / Products.SaveTrackStock for the data + save.
    public async Task<IActionResult> Inventory(int id, int? storeId = null)
    {
        var p = await _db.Products.Where(x => x.Id == id).Select(x => new { x.Id, x.Name }).FirstOrDefaultAsync();
        if (p == null) return NotFound();
        ViewData["Title"] = $"{p.Name} — Inventory";
        ViewBag.ProductId = p.Id;
        ViewBag.ProductName = p.Name;
        ViewBag.BackStoreId = storeId;
        return View();
    }

    // Exact barcode/SKU lookup for the scan box — returns the row data needed to insert/highlight
    // a product inline without reloading the page (so unsaved edits in the grid aren't lost).
    [HttpGet]
    public async Task<IActionResult> ScanLookup(string code, int? storeId = null)
    {
        code = (code ?? "").Trim();
        if (code.Length == 0) return Json(new { found = false });

        var p = await _db.Products
            .Where(x => x.Barcode == code || x.Sku == code
                     || x.Variants.Any(v => v.Barcode == code || v.Sku == code))
            .Select(x => new { x.Id, x.Name, x.Sku, x.Price, Category = x.Category != null ? x.Category.Name : "—",
                               x.IsActive, HasVariants = x.Variants.Any(v => v.IsActive) })
            .FirstOrDefaultAsync();
        if (p == null || !p.IsActive) return Json(new { found = false });

        // Single-location row values for the Stock Management grid.
        var sid = storeId ?? (await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).Select(s => s.Id).FirstOrDefaultAsync());
        var pool = await _db.StoreInventories
            .FirstOrDefaultAsync(si => si.ProductId == p.Id && si.StoreId == sid && si.ProductVariantId == null);

        return Json(new
        {
            found = true,
            productId = p.Id,
            productName = p.Name,
            sku = p.Sku,
            category = p.Category,
            priceText = "₦" + p.Price.ToString("N2"),
            hasVariants = p.HasVariants,
            onHand = pool?.QuantityOnHand ?? 0,
            onOrder = pool?.OnOrder ?? 0,
            min = pool?.MinStock,
            max = pool?.MaxStock
        });
    }

    public class StockEdit
    {
        public int ProductId { get; set; }
        public int? VariantId { get; set; }   // null = product-level pool
        public int StoreId { get; set; }
        public int Quantity { get; set; }
        // Reorder fields (Stock Management grid). Null Min/Max clears the value; null OnOrder leaves it.
        public int? Min { get; set; }
        public int? Max { get; set; }
        public int? OnOrder { get; set; }
        public bool HasReorder { get; set; }   // true when this edit carries Min/Max/OnOrder to persist
    }
    public class BulkStockRequest
    {
        public string? Reason { get; set; }
        public List<StockEdit> Edits { get; set; } = new();
    }

    // Bulk: set stock for many product×store cells, each as a traceable ledger Adjustment
    // tagged with the chosen reason (stock count / received / damage / loss / correction).
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetAll([FromBody] BulkStockRequest req)
    {
        var edits = req?.Edits;
        if (edits == null || edits.Count == 0)
            return Json(new { success = true, count = 0 });

        var reason = string.IsNullOrWhiteSpace(req!.Reason) ? "Stock update" : req.Reason.Trim();
        // Classify the movement from the chosen reason so receipts, damage and shrinkage are
        // first-class (and reportable) instead of all being lumped under "Adjustment". The reason
        // label is still kept on the movement (Reference) for the audit trail.
        var type = AdjustmentReasons.MovementType(reason);
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var validStoreIds = (await _db.Stores.Where(s => s.IsActive).Select(s => s.Id).ToListAsync()).ToHashSet();
        var validProductIds = (await _db.Products
            .Where(p => edits.Select(e => e.ProductId).Distinct().Contains(p.Id))
            .Select(p => p.Id).ToListAsync()).ToHashSet();

        // Validate any submitted variant ids actually belong to their product (so we never
        // materialize a stock row for a bogus or mismatched variant).
        var variantIds = edits.Where(e => e.VariantId.HasValue).Select(e => e.VariantId!.Value).Distinct().ToList();
        var validVariantPairs = variantIds.Count == 0
            ? new HashSet<(int, int)>()
            : (await _db.ProductVariants.Where(v => variantIds.Contains(v.Id))
                .Select(v => new { v.ProductId, v.Id }).ToListAsync())
                .Select(v => (v.ProductId, v.Id)).ToHashSet();

        var valid = edits
            .Where(e => e.Quantity >= 0 && validStoreIds.Contains(e.StoreId) && validProductIds.Contains(e.ProductId)
                && (e.VariantId == null || validVariantPairs.Contains((e.ProductId, e.VariantId.Value))))
            .ToList();
        if (valid.Count == 0) return Json(new { success = true, count = 0 });

        // Store-level authorization (writes-only): reject edits targeting a branch the user
        // isn't assigned to. Reads are open, so the grid may show all branches' columns.
        var writable = await _access.WritableStoreIdsAsync(User);
        if (valid.Any(e => !writable.Contains(e.StoreId)))
            return Json(new { success = false, message = "You can only edit stock for your assigned branch(es)." });

        // Name snapshots for the adjustment lines (so the record reads right even after a rename).
        var pids = valid.Select(e => e.ProductId).Distinct().ToList();
        var pnames = await _db.Products.Where(p => pids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name);
        var vids = valid.Where(e => e.VariantId.HasValue).Select(e => e.VariantId!.Value).Distinct().ToList();
        var vnames = vids.Count == 0 ? new Dictionary<int, string>()
            : await _db.ProductVariants.Where(v => vids.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Name);

        var applied = 0;
        await using var tx = await _db.Database.BeginTransactionAsync();

        // Lock the affected StoreInventory rows (fixed ProductId/StoreId order, matching POS
        // checkout & transfers) so a concurrent sale/transfer/edit on the same cell can't race
        // with the read-compute-write below — without this, the delta is computed from a stale
        // read and the save could either lose an update or throw an unhandled concurrency error.
        if (_db.Database.IsNpgsql()) // FOR UPDATE is Postgres-only (SQLite test harness no-ops)
            foreach (var (pid, sid) in valid.Select(e => (e.ProductId, e.StoreId)).Distinct()
                         .OrderBy(p => p.ProductId).ThenBy(p => p.StoreId))
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM \"StoreInventories\" WHERE \"ProductId\" = {pid} AND \"StoreId\" = {sid} FOR UPDATE");

        // Group the save into one adjustment header per branch (Moniebook "BSA#####"), so the
        // changes are auditable as a unit. Each line still raises a ledger movement that references
        // the adjustment number; the reason rides on the header (and the movement note).
        var seq = await NextAdjustmentSeqAsync();
        foreach (var storeGrp in valid.GroupBy(e => e.StoreId))
        {
            var header = new StockAdjustment
            {
                AdjustmentNumber = $"BSA{seq++:D5}",
                StoreId = storeGrp.Key,
                Reason = reason,
                Source = "Grid",
                CreatedByUserId = userId,
                CreatedAt = DateTime.UtcNow
            };

            foreach (var e in storeGrp)
            {
                // Re-read the EXACT (variant or pool) row under the lock so the delta sets that row to
                // the typed target — not measured against the shared pool via fallback.
                var current = await _stock.GetStockAsync(e.ProductId, e.VariantId, e.StoreId, fallback: false);
                var delta = e.Quantity - current;
                if (delta != 0)
                {
                    var balance = await _stock.ApplyAsync(e.ProductId, e.VariantId, e.StoreId, delta, type,
                        header.AdjustmentNumber, note: reason, userId: userId, materializeVariant: e.VariantId.HasValue);
                    header.Lines.Add(new StockAdjustmentLine
                    {
                        ProductId = e.ProductId,
                        ProductVariantId = e.VariantId,
                        ProductName = pnames.GetValueOrDefault(e.ProductId, ""),
                        VariantName = e.VariantId.HasValue ? vnames.GetValueOrDefault(e.VariantId.Value) : null,
                        QtyDelta = delta,
                        BalanceAfter = balance
                    });
                    applied++;
                }

                // Stock Management grid also persists Min/Max/On-order on the (pool or variant) row.
                if (e.HasReorder)
                {
                    var row = await _db.StoreInventories.FirstOrDefaultAsync(si =>
                        si.ProductId == e.ProductId && si.StoreId == e.StoreId && si.ProductVariantId == e.VariantId);
                    if (row == null)
                    {
                        row = new StoreInventory { ProductId = e.ProductId, StoreId = e.StoreId, ProductVariantId = e.VariantId };
                        _db.StoreInventories.Add(row);
                    }
                    row.MinStock = e.Min;
                    row.MaxStock = e.Max;
                    if (e.OnOrder.HasValue) row.OnOrder = Math.Max(0, e.OnOrder.Value);
                    row.UpdatedAt = DateTime.UtcNow;
                    if (delta == 0) applied++;   // count reorder-only changes
                }
            }

            if (header.Lines.Count > 0) _db.StockAdjustments.Add(header);
        }

        try
        {
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Json(new { success = false, message = "Stock levels changed while saving. Please refresh and try again." });
        }

        if (applied > 0)
            await LogAsync("Update", "Inventory", null, $"Stock adjustment ({reason}) — {applied} change(s)");
        return Json(new { success = true, count = applied });
    }

    // Next sequential BSA number. Computed from the latest row; the caller increments in memory
    // for multiple headers in one save (so they don't collide before SaveChanges).
    private async Task<int> NextAdjustmentSeqAsync()
    {
        var last = await _db.StockAdjustments.OrderByDescending(a => a.Id)
            .Select(a => a.AdjustmentNumber).FirstOrDefaultAsync();
        return last != null && last.StartsWith("BSA") && int.TryParse(last[3..], out var p) ? p + 1 : 1;
    }

    // Export per-branch stock levels to CSV.
    public async Task<IActionResult> ExportCsv(string q = "", int? categoryId = null)
    {
        var stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        var pq = _db.Products.Where(p => p.IsActive);
        if (!string.IsNullOrWhiteSpace(q))
            pq = pq.Where(p => EF.Functions.ILike(p.Name, $"%{q}%")
                            || EF.Functions.ILike(p.Sku ?? "", $"%{q}%")
                            || EF.Functions.ILike(p.Barcode ?? "", $"%{q}%")
                            || p.Variants.Any(v => EF.Functions.ILike(v.Barcode ?? "", $"%{q}%")));

        if (categoryId.HasValue)
            pq = pq.Where(p => p.CategoryId == categoryId.Value);

        var all = await pq.OrderBy(p => p.Name).ToListAsync();
        var ids = all.Select(p => p.Id).ToList();
        var inv = ids.Count > 0
            ? await _db.StoreInventories.Where(si => ids.Contains(si.ProductId)).ToListAsync()
            : new List<StoreInventory>();

        static string Csv(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

        var sb = new StringBuilder();
        sb.Append("Product,SKU,Barcode");
        foreach (var s in stores) sb.Append(',').Append(Csv(s.Name.Replace("Sterlin Glams ", "")));
        sb.AppendLine(",Total");

        foreach (var p in all)
        {
            var byStore = stores.Select(s => inv.Where(si => si.ProductId == p.Id && si.StoreId == s.Id).Sum(si => si.QuantityOnHand)).ToList();
            sb.Append(Csv(p.Name)).Append(',').Append(Csv(p.Sku)).Append(',').Append(Csv(p.Barcode));
            foreach (var v in byStore) sb.Append(',').Append(v);
            sb.Append(',').Append(byStore.Sum()).AppendLine();
        }

        await LogAsync("Export", "Inventory", null, $"Exported stock for {all.Count} product(s) to CSV");
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv", $"stock_{DateTime.UtcNow:yyyyMMdd_HHmm}.csv");
    }
}

// ── Stock Management (single-location) view models ──────────────────────────────
public class StockManagementVm
{
    public List<Store> Stores { get; set; } = new();
    public int SelectedStoreId { get; set; }
    public List<StockMgmtRow> Rows { get; set; } = new();
    public string Query { get; set; } = "";
    public int? CategoryFilter { get; set; }
    public List<Category> Categories { get; set; } = new();
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public int TotalPages { get; set; } = 1;
    public int Total { get; set; }
    public int FirstRow { get; set; }
    public int LastRow { get; set; }
}

public class StockMgmtRow
{
    public int ProductId { get; set; }
    public string Name { get; set; } = "";
    public string? Sku { get; set; }
    public string Category { get; set; } = "";
    public decimal Price { get; set; }
    public int OnHand { get; set; }
    public int OnOrder { get; set; }
    public int? Min { get; set; }
    public int? Max { get; set; }
    public List<StockMgmtVariantRow> Variants { get; set; } = new();
    public bool HasVariants => Variants.Count > 0;
    public int VariantStockSum => Variants.Sum(v => v.OnHand);
}

public class StockMgmtVariantRow
{
    public int VariantId { get; set; }
    public string Name { get; set; } = "";
    public string? Sku { get; set; }
    public int OnHand { get; set; }
    public int OnOrder { get; set; }
    public int? Min { get; set; }
    public int? Max { get; set; }
}
