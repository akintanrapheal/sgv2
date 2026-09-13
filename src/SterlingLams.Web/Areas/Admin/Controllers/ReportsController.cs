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

    // Inclusive from/to as LAGOS dates (defaults to the last 30 days), returned as the UTC window to
    // query plus the local dates to display. See Services/ReportCalendar.
    private static (DateTime From, DateTime ToExclusive, DateTime FromLocal, DateTime ToLocal)
        Range(string? from, string? to) => SterlingLams.Web.Services.ReportCalendar.Range(from, to);

    // Branch attribution below matches Finance: the pickup branch for a POS/collection order,
    // otherwise the branch that fulfilled it. Matching PickupStoreId alone (as this report used to)
    // dropped every online delivery order a branch fulfilled and lumped them into
    // "Online / unassigned", so this report and the Finance dashboard disagreed on branch numbers.

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
        var (f, t, fLocal, tLocal) = Range(from, to);

        // Dated by payment, not creation — matching Finance (see FinanceController.PaidOrders).
        var orders = _db.Orders.Where(o => o.IsPaid
            && (o.PaidAt ?? o.CreatedAt) >= f && (o.PaidAt ?? o.CreatedAt) < t);
        if (storeId.HasValue) orders = orders.Where(o => o.PickupStoreId == storeId || o.FulfillingStoreId == storeId);

        var stores = await _db.Stores.OrderBy(s => s.Name).ToListAsync();
        var storeName = stores.ToDictionary(s => s.Id, s => s.Name);

        var refunds = _db.Refunds.Where(r => r.Status == RefundStatus.Approved && r.CreatedAt >= f && r.CreatedAt < t);
        if (storeId.HasValue) refunds = refunds.Where(r =>
            r.OriginalOrder.PickupStoreId == storeId || r.OriginalOrder.FulfillingStoreId == storeId);
        var refundTotal = await refunds.SumAsync(r => (decimal?)r.Amount) ?? 0;

        // Aggregations run in SQL instead of loading every paid order into memory.
        var totals = await orders.GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Gross = g.Sum(o => o.Total) }).FirstOrDefaultAsync();

        var byPaymentRaw = await orders.GroupBy(o => o.PaymentProvider)
            .Select(g => new { g.Key, Total = g.Sum(o => o.Total) }).ToListAsync();

        // Bucketed on the LAGOS day in memory — the stored timestamps are UTC (see ReportCalendar).
        var byDay = (await orders.Select(o => new { When = o.PaidAt ?? o.CreatedAt, o.Total }).ToListAsync())
            .GroupBy(o => SterlingLams.Web.Services.ReportCalendar.LocalDay(o.When))
            .Select(g => new DayRow(g.Key, g.Count(), g.Sum(o => o.Total)))
            .OrderByDescending(d => d.Day).ToList();

        var byBranchRaw = await orders.GroupBy(o => o.PickupStoreId ?? o.FulfillingStoreId)
            .Select(g => new { g.Key, Count = g.Count(), Total = g.Sum(o => o.Total) }).ToListAsync();

        var vm = new SalesVm
        {
            From = fLocal, To = tLocal, StoreId = storeId, Stores = stores,
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
        var (f, t, fLocal, tLocal) = Range(from, to);
        ViewBag.From = fLocal; ViewBag.To = tLocal; ViewBag.StoreId = storeId;
        ViewBag.Stores = await _db.Stores.OrderBy(s => s.Name).ToListAsync();

        var q = _db.OrderItems.Where(i => i.Order.IsPaid
            && (i.Order.PaidAt ?? i.Order.CreatedAt) >= f && (i.Order.PaidAt ?? i.Order.CreatedAt) < t);
        if (storeId.HasValue) q = q.Where(i => i.Order.PickupStoreId == storeId || i.Order.FulfillingStoreId == storeId);

        var grouped = await q.GroupBy(i => new { i.ProductId, i.ProductName })
            .Select(g => new
            {
                g.Key.ProductId,
                g.Key.ProductName,
                Units = g.Sum(x => x.Quantity),
                // Net of any discount on the line — gross price made discounted lines look like they
                // earned more than they did, and disagreed with the product page's own 90-day figure.
                Revenue = g.Sum(x => x.Quantity * x.UnitPrice - x.DiscountAmount)
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
        /// <summary>Stock on hand valued at RETAIL price — what it would bring in if it all sold.</summary>
        public decimal TotalValue { get; set; }
        /// <summary>Stock on hand valued at COST — what it's worth on the books / for insurance.
        /// Products with no cost price recorded fall back to their retail price, and
        /// <see cref="ItemsMissingCost"/> says how many rows that covers so the figure isn't trusted blindly.</summary>
        public decimal TotalCostValue { get; set; }
        public int ItemsMissingCost { get; set; }
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
            CostValue = g.Sum(si => si.QuantityOnHand * (si.Product.CostPrice ?? si.Product.Price)),
            MissingCost = g.Count(si => si.QuantityOnHand > 0 && si.Product.CostPrice == null),
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
            TotalCostValue = totals?.CostValue ?? 0,
            ItemsMissingCost = totals?.MissingCost ?? 0,
            OutOfStock = totals?.OutOfStock ?? 0,
            ByBranch = byBranch,
            LowStock = lowStock
        };
        return View(vm);
    }

    // ── Order-processing sheet ────────────────────────────────────────────────
    // One row per online order: who it's for, where it goes, who packed/processed it, at which branch,
    // its status and when it was processed. Exportable to CSV. Times shown in Lagos (WAT).
    public record FulfilRow(string OrderNumber, DateTime Placed, string Customer, string? Phone,
        string Address, string Type, string Store, string? PackedBy, DateTime? ProcessedOn,
        string Status, decimal Total);

    private async Task<List<FulfilRow>> FulfilRowsAsync(DateTime f, DateTime t, int? storeId)
    {
        var stores = await _db.Stores.ToDictionaryAsync(s => s.Id, s => s.Name);

        var q = _db.Orders.Where(o => o.Channel == OrderChannel.Online && o.IsPaid
            && (o.PaidAt ?? o.CreatedAt) >= f && (o.PaidAt ?? o.CreatedAt) < t);
        if (storeId.HasValue) q = q.Where(o => o.PickupStoreId == storeId || o.FulfillingStoreId == storeId);

        var raw = await q.OrderByDescending(o => o.PaidAt ?? o.CreatedAt).Take(3000)
            .Select(o => new
            {
                o.OrderNumber,
                Placed = o.PaidAt ?? o.CreatedAt,
                Customer = (o.User.FirstName + " " + o.User.LastName).Trim(),
                Phone = o.User.PhoneNumber,
                o.FulfillmentType,
                o.PickupStoreId,
                o.FulfillingStoreId,
                Addr = o.DeliveryAddress,
                o.PackedByName,
                o.PackedAt,
                o.PickupReadyEmailedAt,
                o.Status,
                o.Total
            }).ToListAsync();

        string StoreOf(int? id) => id.HasValue && stores.TryGetValue(id.Value, out var n) ? n.Replace("Sterlin Glams ", "") : "—";

        return raw.Select(o =>
        {
            var pickup = o.FulfillmentType == FulfillmentType.StorePickup;
            var addr = pickup
                ? "Store pickup" + (o.PickupStoreId.HasValue ? " · " + StoreOf(o.PickupStoreId) : "")
                : (o.Addr != null
                    ? string.Join(", ", new[] { o.Addr.Line1, o.Addr.Line2, o.Addr.City, o.Addr.State }.Where(x => !string.IsNullOrWhiteSpace(x)))
                    : "");
            var processedOn = pickup ? o.PickupReadyEmailedAt : o.PackedAt;
            return new FulfilRow(
                o.OrderNumber,
                SterlingLams.Web.Services.ReportCalendar.ToLocal(o.Placed),
                string.IsNullOrWhiteSpace(o.Customer) ? "Customer" : o.Customer,
                o.Phone,
                addr,
                pickup ? "Store pickup" : "Delivery",
                StoreOf(pickup ? o.PickupStoreId : o.FulfillingStoreId),
                o.PackedByName,
                processedOn.HasValue ? SterlingLams.Web.Services.ReportCalendar.ToLocal(processedOn.Value) : (DateTime?)null,
                o.Status.ToString(),
                o.Total);
        }).ToList();
    }

    public async Task<IActionResult> Fulfilment(string? from, string? to, int? storeId)
    {
        ViewData["Title"] = "Order Processing";
        var (f, t, fLocal, tLocal) = Range(from, to);
        ViewBag.From = fLocal; ViewBag.To = tLocal; ViewBag.StoreId = storeId;
        ViewBag.Stores = await _db.Stores.OrderBy(s => s.Name).ToListAsync();
        return View(await FulfilRowsAsync(f, t, storeId));
    }

    public async Task<IActionResult> FulfilmentCsv(string? from, string? to, int? storeId)
    {
        var (f, t, fLocal, tLocal) = Range(from, to);
        var rows = await FulfilRowsAsync(f, t, storeId);
        var sb = new System.Text.StringBuilder();
        SterlingLams.Web.Services.Csv.AppendRow(sb, "Order", "Placed (WAT)", "Customer", "Phone", "Address",
            "Type", "Store", "Processed by", "Processed on (WAT)", "Status", "Total (₦)");
        foreach (var r in rows)
            SterlingLams.Web.Services.Csv.AppendRow(sb,
                r.OrderNumber, r.Placed.ToString("yyyy-MM-dd HH:mm"), r.Customer, r.Phone ?? "", r.Address,
                r.Type, r.Store, r.PackedBy ?? "", r.ProcessedOn?.ToString("yyyy-MM-dd HH:mm") ?? "",
                r.Status, r.Total.ToString("0"));
        return File(SterlingLams.Web.Services.Csv.ToBytes(sb), "text/csv",
            $"order-processing_{fLocal:yyyyMMdd}-{tLocal:yyyyMMdd}.csv");
    }
}
