using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Admin.Controllers;

/// <summary>
/// Finance department dashboard: money in (paid sales), refunds, net, delivery/logistics fees —
/// broken down over time and by payment channel, branch and cashier, with charts and a CSV export.
/// Read-only. It's its own grantable "Finance" section so a Finance role can be given this alone.
/// </summary>
public class FinanceController : AdminBaseController
{
    protected override string Section => "Finance";

    private readonly ApplicationDbContext _db;
    public FinanceController(ApplicationDbContext db) => _db = db;

    // Inclusive from/to (days). Defaults to the last 30 days.
    private static (DateTime From, DateTime ToExclusive) Range(string? from, string? to)
    {
        var today = DateTime.UtcNow.Date;
        var f = DateTime.TryParse(from, out var pf) ? pf.Date : today.AddDays(-29);
        var t = DateTime.TryParse(to, out var pt) ? pt.Date : today;
        if (t < f) t = f;
        return (DateTime.SpecifyKind(f, DateTimeKind.Utc), DateTime.SpecifyKind(t.AddDays(1), DateTimeKind.Utc));
    }

    public record ChannelPoint(string Channel, decimal Amount, int Count);
    // Order revenue = merchandise (Total minus the delivery charge); Logistics = the in-house
    // delivery fee we collect. Total = the two combined; Net = Total minus refunds.
    public record DayPoint(DateTime Day, int Count, decimal OrderRevenue, decimal Logistics, decimal Refunds)
    { public decimal Total => OrderRevenue + Logistics; public decimal Net => Total - Refunds; }
    public record PeriodPoint(string Label, DateTime Start, int Count, decimal OrderRevenue, decimal Logistics, decimal Refunds)
    { public decimal Total => OrderRevenue + Logistics; public decimal Net => Total - Refunds; }
    public record StorePoint(string Label, int Count, decimal Gross, decimal Refunds, decimal Delivery)
    { public decimal Net => Gross - Refunds; }
    public record StaffPoint(string Name, int Count, decimal Gross);
    public record StatePoint(string State, int Count, decimal Logistics)
    { public decimal AvgFee => Count > 0 ? Logistics / Count : 0; }
    public record DeliveryTypePoint(string Type, int Count, decimal Logistics)
    { public decimal AvgFee => Count > 0 ? Logistics / Count : 0; }

    private static readonly string[] Periods = { "day", "week", "month", "quarter", "year" };

    // Buckets a calendar day into the chosen reporting period (start date + display label).
    private static (string Label, DateTime Start) PeriodBucket(DateTime day, string period)
    {
        switch (period)
        {
            case "week":
                var monday = day.AddDays(-(((int)day.DayOfWeek + 6) % 7)); // ISO week starts Monday
                return ($"Wk of {monday:dd MMM yyyy}", monday);
            case "month":
                var m = new DateTime(day.Year, day.Month, 1);
                return ($"{m:MMM yyyy}", m);
            case "quarter":
                var q = (day.Month - 1) / 3 + 1;
                return ($"Q{q} {day.Year}", new DateTime(day.Year, (q - 1) * 3 + 1, 1));
            case "year":
                return ($"{day.Year}", new DateTime(day.Year, 1, 1));
            default: // day
                return ($"{day:ddd, dd MMM yyyy}", day.Date);
        }
    }

    private static List<PeriodPoint> BucketPeriods(IEnumerable<DayPoint> days, string period) =>
        days.GroupBy(d => PeriodBucket(d.Day, period))
            .Select(g => new PeriodPoint(g.Key.Label, g.Key.Start,
                g.Sum(d => d.Count), g.Sum(d => d.OrderRevenue), g.Sum(d => d.Logistics), g.Sum(d => d.Refunds)))
            .OrderBy(p => p.Start).ToList();

    public class FinanceVm
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public int? StoreId { get; set; }
        public string Channel { get; set; } = "";
        public string Period { get; set; } = "day";
        public List<Store> Stores { get; set; } = new();

        public int Count { get; set; }
        public decimal Gross { get; set; }                       // total revenue (incl. delivery)
        public decimal Refunds { get; set; }
        public decimal DeliveryFees { get; set; }                // logistics revenue
        public decimal OrderRevenue => Gross - DeliveryFees;     // merchandise only
        public decimal LogisticsRevenue => DeliveryFees;
        public decimal Net => Gross - Refunds;
        public decimal Avg => Count > 0 ? Gross / Count : 0;
        public decimal OnlineGross { get; set; }
        public decimal PosGross { get; set; }
        public int OnlineCount { get; set; }
        public int PosCount { get; set; }

        // Money given away (discounts / loyalty redemptions / gift-card spend) — revenue leakage.
        public decimal DiscountTotal { get; set; }
        public decimal LoyaltyTotal { get; set; }
        public decimal GiftCardTotal { get; set; }
        public decimal Giveaway => DiscountTotal + LoyaltyTotal + GiftCardTotal;

        // Previous equal-length period, for period-over-period comparison chips.
        public int PrevCount { get; set; }
        public decimal PrevGross { get; set; }
        public decimal PrevLogistics { get; set; }
        public decimal PrevRefunds { get; set; }
        public decimal PrevOrderRevenue => PrevGross - PrevLogistics;
        public decimal PrevNet => PrevGross - PrevRefunds;

        public List<(string Label, string From, string To)> Presets { get; set; } = new();
        public List<string> Alerts { get; set; } = new();

        public List<PeriodPoint> ByPeriod { get; set; } = new();
        public List<StatePoint> ByState { get; set; } = new();
        public List<DeliveryTypePoint> ByDeliveryType { get; set; } = new();
        public List<ChannelPoint> ByChannel { get; set; } = new();
        public List<StorePoint> ByStore { get; set; } = new();
        public List<StaffPoint> ByStaff { get; set; } = new();
    }

    private IQueryable<Order> PaidOrders(DateTime f, DateTime t, int? storeId, string channel)
    {
        var q = _db.Orders.Where(o => o.IsPaid && o.CreatedAt >= f && o.CreatedAt < t);
        if (storeId.HasValue) q = q.Where(o => o.PickupStoreId == storeId || o.FulfillingStoreId == storeId);
        if (channel == "Online") q = q.Where(o => o.Channel == OrderChannel.Online);
        else if (channel == "Pos") q = q.Where(o => o.Channel == OrderChannel.Pos);
        return q;
    }

    // Lightweight totals for a window — used for the previous-period comparison.
    private async Task<(int Count, decimal Gross, decimal Logistics, decimal Refunds)> SnapshotAsync(
        DateTime f, DateTime t, int? storeId, string channel)
    {
        var g = await PaidOrders(f, t, storeId, channel).GroupBy(_ => 1)
            .Select(x => new { Count = x.Count(), Gross = x.Sum(o => o.Total), Logistics = x.Sum(o => o.DeliveryFee) })
            .FirstOrDefaultAsync();
        var refq = _db.Refunds.Where(r => r.CreatedAt >= f && r.CreatedAt < t);
        if (storeId.HasValue) refq = refq.Where(r => r.OriginalOrder.PickupStoreId == storeId || r.OriginalOrder.FulfillingStoreId == storeId);
        if (channel == "Online") refq = refq.Where(r => r.OriginalOrder.Channel == OrderChannel.Online);
        else if (channel == "Pos") refq = refq.Where(r => r.OriginalOrder.Channel == OrderChannel.Pos);
        var refunds = await refq.SumAsync(r => (decimal?)r.Amount) ?? 0;
        return (g?.Count ?? 0, g?.Gross ?? 0, g?.Logistics ?? 0, refunds);
    }

    // Date-range quick presets (This month, Last month, QTD, YTD, last 30 days).
    private static List<(string Label, string From, string To)> BuildPresets()
    {
        var today = DateTime.UtcNow.Date;
        string S(DateTime d) => d.ToString("yyyy-MM-dd");
        var thisMonth = new DateTime(today.Year, today.Month, 1);
        var lastMonth = thisMonth.AddMonths(-1);
        var qStart = new DateTime(today.Year, (today.Month - 1) / 3 * 3 + 1, 1);
        return new()
        {
            ("Last 30 days", S(today.AddDays(-29)), S(today)),
            ("This month",   S(thisMonth),          S(today)),
            ("Last month",   S(lastMonth),          S(thisMonth.AddDays(-1))),
            ("This quarter", S(qStart),             S(today)),
            ("Year to date", S(new DateTime(today.Year, 1, 1)), S(today)),
        };
    }

    public async Task<IActionResult> Index(string? from, string? to, int? storeId, string? channel, string? period)
    {
        ViewData["Title"] = "Finance";
        var vm = await BuildAsync(from, to, storeId, channel, period);
        return View(vm);
    }

    // CSV export of the same figures the dashboard shows — finance always wants the numbers in a sheet.
    public async Task<IActionResult> Export(string? from, string? to, int? storeId, string? channel, string? period)
    {
        var vm = await BuildAsync(from, to, storeId, channel, period);
        var storeName = vm.StoreId.HasValue ? vm.Stores.FirstOrDefault(s => s.Id == vm.StoreId)?.Name ?? "All" : "All";
        var channelName = vm.Channel switch { "Online" => "Online", "Pos" => "POS", _ => "All" };
        var totalChannel = vm.ByChannel.Sum(c => c.Amount);

        var sb = new StringBuilder();
        static string Q(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        void Row(params string[] cells) => sb.AppendLine(string.Join(",", cells.Select(Q)));

        var periodName = vm.Period switch { "week" => "Weekly", "month" => "Monthly", "quarter" => "Quarterly", "year" => "Yearly", _ => "Daily" };
        Row("Sterlin Glams — Finance report");
        Row("Range", $"{vm.From:yyyy-MM-dd} to {vm.To:yyyy-MM-dd}");
        Row("Branch", storeName);
        Row("Channel", channelName);
        Row("Grouped by", periodName);
        sb.AppendLine();
        Row("Summary");
        Row("Order revenue", vm.OrderRevenue.ToString("0.##"));
        Row("Logistics revenue", vm.LogisticsRevenue.ToString("0.##"));
        Row("Total revenue", vm.Gross.ToString("0.##"));
        Row("Refunds", vm.Refunds.ToString("0.##"));
        Row("Net", vm.Net.ToString("0.##"));
        Row("Transactions", vm.Count.ToString());
        Row("Average order", vm.Avg.ToString("0.##"));
        Row("Online revenue", vm.OnlineGross.ToString("0.##"));
        Row("POS revenue", vm.PosGross.ToString("0.##"));
        sb.AppendLine();
        Row("Payment channels", "Amount", "Transactions", "Share %");
        foreach (var c in vm.ByChannel)
            Row(c.Channel, c.Amount.ToString("0.##"), c.Count.ToString(),
                (totalChannel > 0 ? c.Amount / totalChannel * 100 : 0).ToString("0.#"));
        sb.AppendLine();
        Row("By branch", "Transactions", "Gross", "Refunds", "Net", "Delivery fees");
        foreach (var s in vm.ByStore)
            Row(s.Label, s.Count.ToString(), s.Gross.ToString("0.##"), s.Refunds.ToString("0.##"), s.Net.ToString("0.##"), s.Delivery.ToString("0.##"));
        sb.AppendLine();
        Row("By cashier (POS)", "Transactions", "Gross");
        foreach (var s in vm.ByStaff)
            Row(s.Name, s.Count.ToString(), s.Gross.ToString("0.##"));
        sb.AppendLine();
        Row("Logistics by delivery type", "Orders", "Logistics revenue", "Avg fee");
        foreach (var d in vm.ByDeliveryType)
            Row(d.Type, d.Count.ToString(), d.Logistics.ToString("0.##"), d.AvgFee.ToString("0.##"));
        sb.AppendLine();
        Row("Logistics by state", "Orders", "Logistics revenue", "Avg fee");
        foreach (var s in vm.ByState)
            Row(s.State, s.Count.ToString(), s.Logistics.ToString("0.##"), s.AvgFee.ToString("0.##"));
        sb.AppendLine();
        Row(periodName, "Transactions", "Order revenue", "Logistics revenue", "Total", "Refunds", "Net");
        foreach (var p in vm.ByPeriod)
            Row(p.Label, p.Count.ToString(), p.OrderRevenue.ToString("0.##"), p.Logistics.ToString("0.##"),
                p.Total.ToString("0.##"), p.Refunds.ToString("0.##"), p.Net.ToString("0.##"));

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv", $"finance_{vm.From:yyyyMMdd}-{vm.To:yyyyMMdd}.csv");
    }

    private async Task<FinanceVm> BuildAsync(string? from, string? to, int? storeId, string? channel, string? period)
    {
        var (f, t) = Range(from, to);
        channel = channel is "Online" or "Pos" ? channel : "";
        period = Periods.Contains(period) ? period! : "day";

        var stores = await _db.Stores.OrderBy(s => s.Name).ToListAsync();
        var storeName = stores.ToDictionary(s => s.Id, s => s.Name);

        var paid = PaidOrders(f, t, storeId, channel);

        // Headline + online/POS split + giveaways (discounts/loyalty/gift cards) in one grouped query.
        var chan = await paid.GroupBy(o => o.Channel)
            .Select(g => new
            {
                g.Key,
                Count = g.Count(),
                Gross = g.Sum(o => o.Total),
                Delivery = g.Sum(o => o.DeliveryFee),
                Disc = g.Sum(o => o.DiscountAmount),
                Loy = g.Sum(o => o.LoyaltyDiscount),
                Gift = g.Sum(o => o.GiftCardAmount)
            })
            .ToListAsync();
        var posGross = chan.Where(c => c.Key == OrderChannel.Pos).Sum(c => c.Gross);
        var posCount = chan.Where(c => c.Key == OrderChannel.Pos).Sum(c => c.Count);
        var onlineGross = chan.Where(c => c.Key == OrderChannel.Online).Sum(c => c.Gross);
        var onlineCount = chan.Where(c => c.Key == OrderChannel.Online).Sum(c => c.Count);

        // Refunds in range, attributed via the original order (respects the same filters).
        var refq = _db.Refunds.Where(r => r.CreatedAt >= f && r.CreatedAt < t);
        if (storeId.HasValue) refq = refq.Where(r => r.OriginalOrder.PickupStoreId == storeId || r.OriginalOrder.FulfillingStoreId == storeId);
        if (channel == "Online") refq = refq.Where(r => r.OriginalOrder.Channel == OrderChannel.Online);
        else if (channel == "Pos") refq = refq.Where(r => r.OriginalOrder.Channel == OrderChannel.Pos);
        var refundTotal = await refq.SumAsync(r => (decimal?)r.Amount) ?? 0;

        // By day: gross/count from orders, refunds from refunds, merged (incl. refund-only days).
        var grossByDay = await paid.GroupBy(o => o.CreatedAt.Date)
            .Select(g => new { Day = g.Key, Count = g.Count(), Total = g.Sum(o => o.Total), Logistics = g.Sum(o => o.DeliveryFee) }).ToListAsync();
        var refByDay = await refq.GroupBy(r => r.CreatedAt.Date)
            .Select(g => new { Day = g.Key, Refunds = g.Sum(x => x.Amount) }).ToListAsync();
        var refDayMap = refByDay.ToDictionary(x => x.Day, x => x.Refunds);
        var byDay = grossByDay
            .Select(x => new DayPoint(x.Day, x.Count, x.Total - x.Logistics, x.Logistics, refDayMap.GetValueOrDefault(x.Day, 0)))
            .ToList();
        foreach (var r in refByDay.Where(r => grossByDay.All(g => g.Day != r.Day)))
            byDay.Add(new DayPoint(r.Day, 0, 0, 0, r.Refunds));
        var byPeriod = BucketPeriods(byDay, period);

        // By branch (POS uses PickupStoreId; delivery-from-branch uses FulfillingStoreId).
        var grossByStore = await paid.GroupBy(o => o.PickupStoreId ?? o.FulfillingStoreId)
            .Select(g => new { StoreId = g.Key, Count = g.Count(), Gross = g.Sum(o => o.Total), Delivery = g.Sum(o => o.DeliveryFee) })
            .ToListAsync();
        var refByStore = await refq.GroupBy(r => r.OriginalOrder.PickupStoreId ?? r.OriginalOrder.FulfillingStoreId)
            .Select(g => new { StoreId = g.Key, Refunds = g.Sum(x => x.Amount) }).ToListAsync();
        var refStoreMap = refByStore.ToDictionary(x => x.StoreId ?? -1, x => x.Refunds);
        var byStore = grossByStore.Select(x => new StorePoint(
                x.StoreId.HasValue && storeName.ContainsKey(x.StoreId.Value) ? storeName[x.StoreId.Value] : "Online / unassigned",
                x.Count, x.Gross, refStoreMap.GetValueOrDefault(x.StoreId ?? -1, 0), x.Delivery))
            .OrderByDescending(s => s.Gross).ToList();

        // By cashier — POS only (on a POS sale Order.UserId is the cashier).
        var staffRaw = await paid.Where(o => o.Channel == OrderChannel.Pos)
            .GroupBy(o => o.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count(), Gross = g.Sum(o => o.Total) }).ToListAsync();
        var staffIds = staffRaw.Select(s => s.UserId).ToList();
        var users = await _db.Users.Where(u => staffIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FirstName, u.LastName, u.UserName }).ToListAsync();
        var nameMap = users.ToDictionary(u => u.Id, u =>
        {
            var n = $"{u.FirstName} {u.LastName}".Trim();
            return string.IsNullOrWhiteSpace(n) ? (u.UserName ?? "—") : n;
        });
        var byStaff = staffRaw
            .Select(s => new StaffPoint(nameMap.GetValueOrDefault(s.UserId, "Unknown"), s.Count, s.Gross))
            .OrderByDescending(s => s.Gross).ToList();

        // Logistics revenue (in-house delivery) broken down by destination state and by
        // Express vs Standard delivery. Only delivery orders that carry a fee.
        var deliveries = paid.Where(o => o.FulfillmentType == FulfillmentType.Delivery && o.DeliveryFee > 0);

        var stateRaw = await deliveries.Where(o => o.DeliveryAddressId != null)
            .GroupBy(o => o.DeliveryAddress!.State)
            .Select(g => new { State = g.Key, Count = g.Count(), Logistics = g.Sum(o => o.DeliveryFee) })
            .ToListAsync();
        var byState = stateRaw
            .Select(x => new StatePoint(string.IsNullOrWhiteSpace(x.State) ? "Unspecified" : x.State.Trim(), x.Count, x.Logistics))
            .OrderByDescending(s => s.Logistics).ToList();

        var typeRaw = await deliveries
            .GroupBy(o => o.DeliveryType)
            .Select(g => new { Type = g.Key, Count = g.Count(), Logistics = g.Sum(o => o.DeliveryFee) })
            .ToListAsync();
        var byDeliveryType = typeRaw
            .Select(x => new DeliveryTypePoint(string.IsNullOrWhiteSpace(x.Type) ? "Unspecified" : x.Type!.Trim(), x.Count, x.Logistics))
            .OrderByDescending(d => d.Logistics).ToList();

        // Payment channels: explicit POS tenders (Cash/Card/Transfer) + provider fallback
        // (e.g. Paystack for online, legacy POS) for orders that carry no tender rows.
        var payQ = _db.OrderPayments.Where(p => p.Order.IsPaid && p.Order.CreatedAt >= f && p.Order.CreatedAt < t);
        if (storeId.HasValue) payQ = payQ.Where(p => p.Order.PickupStoreId == storeId || p.Order.FulfillingStoreId == storeId);
        if (channel == "Online") payQ = payQ.Where(p => p.Order.Channel == OrderChannel.Online);
        else if (channel == "Pos") payQ = payQ.Where(p => p.Order.Channel == OrderChannel.Pos);
        var byMethod = await payQ.GroupBy(p => p.Method)
            .Select(g => new { Channel = g.Key, Amount = g.Sum(x => x.Amount), Count = g.Count() }).ToListAsync();

        var fbQ = paid.Where(o => !_db.OrderPayments.Any(p => p.OrderId == o.Id));
        var byProvider = await fbQ.GroupBy(o => o.PaymentProvider)
            .Select(g => new { Channel = g.Key, Amount = g.Sum(o => o.Total), Count = g.Count() }).ToListAsync();

        var channelMap = new Dictionary<string, (decimal Amount, int Count)>(StringComparer.OrdinalIgnoreCase);
        void AddCh(string? label, decimal amt, int cnt)
        {
            var key = string.IsNullOrWhiteSpace(label) ? "Other" : label.Trim();
            var cur = channelMap.GetValueOrDefault(key);
            channelMap[key] = (cur.Amount + amt, cur.Count + cnt);
        }
        foreach (var m in byMethod) AddCh(m.Channel, m.Amount, m.Count);
        foreach (var p in byProvider) AddCh(p.Channel, p.Amount, p.Count);
        var byChannel = channelMap
            .Select(kv => new ChannelPoint(kv.Key, kv.Value.Amount, kv.Value.Count))
            .OrderByDescending(c => c.Amount).ToList();

        var gross = chan.Sum(c => c.Gross);
        var discountTotal = chan.Sum(c => c.Disc);
        var loyaltyTotal = chan.Sum(c => c.Loy);
        var giftCardTotal = chan.Sum(c => c.Gift);

        // Previous equal-length window immediately before this one (for comparison chips).
        var prev = await SnapshotAsync(f - (t - f), f, storeId, channel);

        // Anomaly flags — quick things finance should look at.
        var alerts = new List<string>();
        if (gross > 0 && refundTotal / gross >= 0.15m)
            alerts.Add($"Refunds are {refundTotal / gross:P0} of gross revenue (₦{refundTotal:N0}) — worth reviewing returns.");
        var negPeriods = byPeriod.Count(p => p.Net < 0);
        if (negPeriods > 0)
            alerts.Add($"{negPeriods} {period}(s) closed with negative net — refunds exceeded sales.");
        var giveaway = discountTotal + loyaltyTotal + giftCardTotal;
        if (gross > 0 && giveaway / gross >= 0.15m)
            alerts.Add($"Discounts &amp; giveaways are {giveaway / gross:P0} of gross (₦{giveaway:N0}).");

        return new FinanceVm
        {
            From = f,
            To = t.AddDays(-1),
            StoreId = storeId,
            Channel = channel,
            Period = period,
            Stores = stores,
            Count = chan.Sum(c => c.Count),
            Gross = gross,
            Refunds = refundTotal,
            DeliveryFees = chan.Sum(c => c.Delivery),
            OnlineGross = onlineGross,
            PosGross = posGross,
            OnlineCount = onlineCount,
            PosCount = posCount,
            DiscountTotal = discountTotal,
            LoyaltyTotal = loyaltyTotal,
            GiftCardTotal = giftCardTotal,
            PrevCount = prev.Count,
            PrevGross = prev.Gross,
            PrevLogistics = prev.Logistics,
            PrevRefunds = prev.Refunds,
            Presets = BuildPresets(),
            Alerts = alerts,
            ByPeriod = byPeriod,
            ByState = byState,
            ByDeliveryType = byDeliveryType,
            ByChannel = byChannel,
            ByStore = byStore,
            ByStaff = byStaff
        };
    }
}
