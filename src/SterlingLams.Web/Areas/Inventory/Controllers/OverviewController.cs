using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

public class OverviewController : InventoryAreaController
{
    private readonly ApplicationDbContext _db;
    public OverviewController(ApplicationDbContext db) => _db = db;

    public async Task<IActionResult> Index(int days = 30, int? storeId = null)
    {
        ViewData["Title"] = "Inventory Overview";
        if (days != 7 && days != 30 && days != 90) days = 30;
        var today = DateTime.UtcNow.Date;

        var stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        if (storeId.HasValue && stores.All(s => s.Id != storeId)) storeId = null; // ignore unknown branch
        ViewBag.Days = days; ViewBag.StoreId = storeId; ViewBag.Stores = stores;

        // Product-level stock totals. Done as a flat product read + ONE grouped inventory aggregate
        // (instead of a correlated SUM subquery per product, which scanned StoreInventories ~8k times
        // and was the Overview's main bottleneck).
        var prods = await _db.Products.Where(p => p.IsActive)
            .Select(p => new { p.Id, p.Name, p.LowStockThreshold })
            .ToListAsync();
        var totalsById = await _db.StoreInventories
            .Where(si => storeId == null || si.StoreId == storeId)
            .GroupBy(si => si.ProductId)
            .Select(g => new { ProductId = g.Key, Total = g.Sum(x => x.QuantityOnHand) })
            .ToDictionaryAsync(x => x.ProductId, x => x.Total);

        int Thr(int t) => Math.Max(1, t);
        var prod = prods.Select(p => new
        {
            p.Name, p.LowStockThreshold,
            Total = totalsById.TryGetValue(p.Id, out var t) ? t : 0
        }).ToList();

        var vm = new InventoryOverviewViewModel
        {
            TotalSkus   = prod.Count,
            OutOfStock  = prod.Count(x => x.Total == 0),
            LowStock    = prod.Count(x => x.Total > 0 && x.Total <= Thr(x.LowStockThreshold)),
            UnitsOnHand = prod.Sum(x => x.Total),
            Alerts = prod.Where(x => x.Total <= Thr(x.LowStockThreshold))
                         .OrderBy(x => x.Total).ThenBy(x => x.Name).Take(12)
                         .Select(x => new StockAlertRow { Name = x.Name, Total = x.Total, Threshold = x.LowStockThreshold })
                         .ToList(),
        };

        // Units per branch.
        var byStore = await _db.StoreInventories.GroupBy(si => si.StoreId)
            .Select(g => new { StoreId = g.Key, Units = g.Sum(x => x.QuantityOnHand) })
            .ToListAsync();
        vm.PerBranch = stores.Select(s => new BranchUnitsRow
        {
            Store = s.Name.Replace("Sterlin Glams ", ""),
            Units = byStore.FirstOrDefault(b => b.StoreId == s.Id)?.Units ?? 0
        }).ToList();

        // Till summary.
        vm.OpenSessions = await _db.TillSessions.CountAsync(s => s.ClosedAt == null
            && (storeId == null || s.Register!.StoreId == storeId));
        var posToday = _db.Orders.Where(o => o.Channel == OrderChannel.Pos && o.CreatedAt >= today
            && (storeId == null || o.PickupStoreId == storeId));
        vm.TillSalesToday = await posToday.SumAsync(o => (decimal?)o.Total) ?? 0;
        vm.TillTxToday = await posToday.CountAsync();

        // ── Sales insight: selected window, excluding cancelled/refunded, scoped to branch. ──────
        var since = today.AddDays(-(days - 1));
        var soldOrders = _db.Orders.Where(o => o.CreatedAt >= since
            && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Refunded
            && (storeId == null || o.PickupStoreId == storeId || o.FulfillingStoreId == storeId));

        // Top products by units sold (join OrderItems → Orders via the nav with the same window/scope,
        // instead of an EXISTS subquery evaluated per order item across the whole table).
        vm.TopProducts = await _db.OrderItems
            .Where(oi => oi.Order.CreatedAt >= since
                && oi.Order.Status != OrderStatus.Cancelled && oi.Order.Status != OrderStatus.Refunded
                && (storeId == null || oi.Order.PickupStoreId == storeId || oi.Order.FulfillingStoreId == storeId))
            .GroupBy(oi => oi.ProductName)
            .Select(g => new TopProductRow
            {
                Name = g.Key,
                Units = g.Sum(x => x.Quantity),
                Revenue = g.Sum(x => (x.Quantity * x.UnitPrice) - x.DiscountAmount)
            })
            .OrderByDescending(x => x.Units).Take(5)
            .ToListAsync();

        // Top staff by POS sales (UserId = cashier on POS orders).
        var staffAgg = await soldOrders.Where(o => o.Channel == OrderChannel.Pos)
            .GroupBy(o => o.UserId)
            .Select(g => new { UserId = g.Key, Sales = g.Sum(x => x.Total), Tx = g.Count() })
            .OrderByDescending(x => x.Sales).Take(5)
            .ToListAsync();
        var staffIds = staffAgg.Select(s => s.UserId).ToList();
        var staffNames = await _db.Users.Where(u => staffIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Email }).ToListAsync();
        vm.TopStaff = staffAgg.Select(s => new TopStaffRow
        {
            Name = staffNames.FirstOrDefault(n => n.Id == s.UserId)?.Email ?? "—",
            Sales = s.Sales,
            Transactions = s.Tx
        }).ToList();

        // Daily sales trend (last 30 days) — revenue + order count per day, for the line chart.
        var trendRaw = await soldOrders
            .GroupBy(o => o.CreatedAt.Date)
            .Select(g => new { Date = g.Key, Revenue = g.Sum(o => o.Total), Orders = g.Count() })
            .ToListAsync();
        var trend = new List<DayPointRow>();
        for (int i = days - 1; i >= 0; i--)
        {
            var date = today.AddDays(-i);
            var row = trendRaw.FirstOrDefault(r => r.Date == date);
            trend.Add(new DayPointRow { Label = date.ToString("MMM dd"), Revenue = row?.Revenue ?? 0, Orders = row?.Orders ?? 0 });
        }
        vm.SalesTrend = trend;

        // Recent stock movements.
        vm.RecentMovements = await _db.StockMovements
            .Where(m => storeId == null || m.StoreId == storeId)
            .OrderByDescending(m => m.Id).Take(8)
            .Select(m => new MovementRow
            {
                Product = m.Product.Name,
                Store = m.Store.Name.Replace("Sterlin Glams ", ""),
                Change = m.QuantityChange,
                Type = m.Type.ToString(),
                When = m.CreatedAt
            })
            .ToListAsync();

        return View(vm);
    }
}

public class InventoryOverviewViewModel
{
    public int TotalSkus { get; set; }
    public int OutOfStock { get; set; }
    public int LowStock { get; set; }
    public int UnitsOnHand { get; set; }
    public List<BranchUnitsRow> PerBranch { get; set; } = new();
    public List<StockAlertRow> Alerts { get; set; } = new();
    public int OpenSessions { get; set; }
    public decimal TillSalesToday { get; set; }
    public int TillTxToday { get; set; }
    public List<MovementRow> RecentMovements { get; set; } = new();
    public List<TopProductRow> TopProducts { get; set; } = new();
    public List<TopStaffRow> TopStaff { get; set; } = new();
    public List<DayPointRow> SalesTrend { get; set; } = new();

    // Stock-health split for the doughnut (derived from the counts above).
    public int InStock => Math.Max(0, TotalSkus - OutOfStock - LowStock);
}
public class DayPointRow { public string Label { get; set; } = ""; public decimal Revenue { get; set; } public int Orders { get; set; } }
public class TopProductRow { public string Name { get; set; } = ""; public int Units { get; set; } public decimal Revenue { get; set; } }
public class TopStaffRow { public string Name { get; set; } = ""; public decimal Sales { get; set; } public int Transactions { get; set; } }
public class BranchUnitsRow { public string Store { get; set; } = ""; public int Units { get; set; } }
public class StockAlertRow { public string Name { get; set; } = ""; public int Total { get; set; } public int Threshold { get; set; } }
public class MovementRow { public string Product { get; set; } = ""; public string Store { get; set; } = ""; public int Change { get; set; } public string Type { get; set; } = ""; public DateTime When { get; set; } }
