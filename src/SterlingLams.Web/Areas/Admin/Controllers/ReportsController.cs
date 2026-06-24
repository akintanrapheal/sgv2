using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Admin.Controllers;

public class ReportsController : AdminBaseController
{
    protected override string Section => "Reports";

    private readonly ApplicationDbContext _db;
    public ReportsController(ApplicationDbContext db) => _db = db;

    public IActionResult Index() => RedirectToAction(nameof(Sales));

    // Resolve the from/to range (inclusive days). Defaults to the last 30 days.
    private static (DateTime From, DateTime ToExclusive) Range(string? from, string? to)
    {
        var today = DateTime.UtcNow.Date;
        var f = DateTime.TryParse(from, out var pf) ? pf.Date : today.AddDays(-29);
        var t = DateTime.TryParse(to, out var pt) ? pt.Date : today;
        if (t < f) t = f;
        return (DateTime.SpecifyKind(f, DateTimeKind.Utc), DateTime.SpecifyKind(t.AddDays(1), DateTimeKind.Utc));
    }

    public record KV(string Label, decimal Amount);
    public record DayRow(DateTime Day, int Count, decimal Total);
    public record BranchRow(string Label, int Count, decimal Total);

    public class SalesVm
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public int? StoreId { get; set; }
        public List<Store> Stores { get; set; } = new();
        public int Count { get; set; }
        public decimal Gross { get; set; }
        public decimal Refunds { get; set; }
        public decimal Net => Gross - Refunds;
        public decimal Avg => Count > 0 ? Gross / Count : 0;
        public List<KV> ByPayment { get; set; } = new();
        public List<DayRow> ByDay { get; set; } = new();
        public List<BranchRow> ByBranch { get; set; } = new();
    }

    public async Task<IActionResult> Sales(string? from, string? to, int? storeId)
    {
        ViewData["Title"] = "Sales Report";
        var (f, t) = Range(from, to);

        var orders = _db.Orders.Where(o => o.IsPaid && o.CreatedAt >= f && o.CreatedAt < t);
        if (storeId.HasValue) orders = orders.Where(o => o.PickupStoreId == storeId);

        var stores = await _db.Stores.OrderBy(s => s.Name).ToListAsync();
        var storeName = stores.ToDictionary(s => s.Id, s => s.Name);

        var refunds = _db.Refunds.Where(r => r.CreatedAt >= f && r.CreatedAt < t);
        if (storeId.HasValue) refunds = refunds.Where(r => r.OriginalOrder.PickupStoreId == storeId);
        var refundTotal = await refunds.SumAsync(r => (decimal?)r.Amount) ?? 0;

        // Aggregations run in SQL instead of loading every paid order into memory.
        var totals = await orders.GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Gross = g.Sum(o => o.Total) }).FirstOrDefaultAsync();

        var byPaymentRaw = await orders.GroupBy(o => o.PaymentProvider)
            .Select(g => new { g.Key, Total = g.Sum(o => o.Total) }).ToListAsync();

        var byDay = (await orders.GroupBy(o => o.CreatedAt.Date)
                .Select(g => new { Day = g.Key, Count = g.Count(), Total = g.Sum(o => o.Total) })
                .ToListAsync())
            .Select(x => new DayRow(x.Day, x.Count, x.Total))
            .OrderByDescending(d => d.Day).ToList();

        var byBranchRaw = await orders.GroupBy(o => o.PickupStoreId)
            .Select(g => new { g.Key, Count = g.Count(), Total = g.Sum(o => o.Total) }).ToListAsync();

        var vm = new SalesVm
        {
            From = f, To = t.AddDays(-1), StoreId = storeId, Stores = stores,
            Count = totals?.Count ?? 0,
            Gross = totals?.Gross ?? 0,
            Refunds = refundTotal,
            // Null/empty providers collapse to "Other" (re-grouped here since SQL keeps them distinct).
            ByPayment = byPaymentRaw
                .GroupBy(x => string.IsNullOrEmpty(x.Key) ? "Other" : x.Key)
                .Select(g => new KV(g.Key, g.Sum(x => x.Total)))
                .OrderByDescending(k => k.Amount).ToList(),
            ByDay = byDay,
            ByBranch = byBranchRaw
                .Select(x => new BranchRow(
                    x.Key.HasValue && storeName.ContainsKey(x.Key.Value) ? storeName[x.Key.Value] : "Online / unassigned",
                    x.Count, x.Total))
                .OrderByDescending(b => b.Total).ToList()
        };
        return View(vm);
    }

    public record ProductRow(string Name, string? Sku, int Units, decimal Revenue);

    public async Task<IActionResult> Products(string? from, string? to, int? storeId)
    {
        ViewData["Title"] = "Best Sellers";
        var (f, t) = Range(from, to);
        ViewBag.From = f; ViewBag.To = t.AddDays(-1); ViewBag.StoreId = storeId;
        ViewBag.Stores = await _db.Stores.OrderBy(s => s.Name).ToListAsync();

        var q = _db.OrderItems.Where(i => i.Order.IsPaid && i.Order.CreatedAt >= f && i.Order.CreatedAt < t);
        if (storeId.HasValue) q = q.Where(i => i.Order.PickupStoreId == storeId);

        var grouped = await q.GroupBy(i => new { i.ProductId, i.ProductName })
            .Select(g => new
            {
                g.Key.ProductId,
                g.Key.ProductName,
                Units = g.Sum(x => x.Quantity),
                Revenue = g.Sum(x => x.Quantity * x.UnitPrice)
            })
            .OrderByDescending(r => r.Revenue)
            .Take(100)
            .ToListAsync();

        var ids = grouped.Select(g => g.ProductId).ToList();
        var skus = await _db.Products.Where(p => ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Sku);

        var rows = grouped
            .Select(g => new ProductRow(g.ProductName, skus.GetValueOrDefault(g.ProductId), g.Units, g.Revenue))
            .ToList();
        return View(rows);
    }

    public class StockVm
    {
        public decimal TotalValue { get; set; }
        public int TotalUnits { get; set; }
        public List<BranchRow> ByBranch { get; set; } = new();
        public List<LowStockRow> LowStock { get; set; } = new();
        public int OutOfStock { get; set; }
    }
    public record LowStockRow(string Product, string Store, int Qty, int Threshold);

    public async Task<IActionResult> Stock()
    {
        ViewData["Title"] = "Stock Report";

        // Aggregation pushed to SQL — don't pull the whole inventory table into memory.
        var inv = _db.StoreInventories.Where(si => si.Product.IsActive);

        var totals = await inv.GroupBy(_ => 1).Select(g => new
        {
            Units = g.Sum(si => si.QuantityOnHand),
            Value = g.Sum(si => si.QuantityOnHand * si.Product.Price),
            OutOfStock = g.Count(si => si.QuantityOnHand <= 0)
        }).FirstOrDefaultAsync();

        var byBranch = (await inv.GroupBy(si => si.Store.Name)
                .Select(g => new { Name = g.Key, Units = g.Sum(si => si.QuantityOnHand), Value = g.Sum(si => si.QuantityOnHand * si.Product.Price) })
                .ToListAsync())
            .Select(x => new BranchRow(x.Name, x.Units, x.Value))
            .OrderByDescending(b => b.Total).ToList();

        var lowStock = await inv
            .Where(si => si.QuantityOnHand > 0 && si.QuantityOnHand <= si.Product.LowStockThreshold)
            .OrderBy(si => si.QuantityOnHand)
            .Select(si => new LowStockRow(si.Product.Name, si.Store.Name, si.QuantityOnHand, si.Product.LowStockThreshold))
            .Take(100).ToListAsync();

        var vm = new StockVm
        {
            TotalUnits = totals?.Units ?? 0,
            TotalValue = totals?.Value ?? 0,
            OutOfStock = totals?.OutOfStock ?? 0,
            ByBranch = byBranch,
            LowStock = lowStock
        };
        return View(vm);
    }
}
