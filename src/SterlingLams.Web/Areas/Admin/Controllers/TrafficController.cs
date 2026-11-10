using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers;

/// <summary>Admin → Traffic: storefront page-view analytics from our own request log (TrafficHits).
/// Grantable section ("Traffic") — read-only analytics, so view is enough. (Owner account bypasses all.)</summary>
public class TrafficController : AdminBaseController
{
    protected override string? Section => "Traffic";

    private readonly ApplicationDbContext _db;
    private readonly ICloudflareAnalytics _cf;
    private readonly ISettingsService _settings;
    private readonly ISettingsSecretProtector _secrets;

    public TrafficController(ApplicationDbContext db, ICloudflareAnalytics cf,
        ISettingsService settings, ISettingsSecretProtector secrets)
    {
        _db = db;
        _cf = cf;
        _settings = settings;
        _secrets = secrets;
    }

    public async Task<IActionResult> Index(int days = 30, string bots = "all")
    {
        days = days is 7 or 30 or 90 ? days : 30;
        var humanOnly = bots == "human";
        ViewData["Title"] = "Traffic";

        var start = DateTime.UtcNow.Date.AddDays(-(days - 1));   // start of the earliest day in range (UTC)
        var qAll = _db.TrafficHits.AsNoTracking().Where(t => t.CreatedAt >= start);
        // Everything below respects the Humans-only toggle; the human/bot split is always from the full set.
        var q = humanOnly ? qAll.Where(t => t.Device != "Bot") : qAll;

        var vm = new TrafficViewModel { Days = days, HumanOnly = humanOnly };
        vm.HumanViews    = await qAll.CountAsync(t => t.Device != "Bot");
        vm.BotViews      = await qAll.CountAsync(t => t.Device == "Bot");
        vm.TotalViews    = await q.CountAsync();
        vm.TotalVisitors = await q.Select(t => t.VisitorKey).Distinct().CountAsync();
        // Human visitors (bots never buy) — the honest denominator for the conversion rate, independent
        // of the All/Humans toggle above.
        vm.HumanVisitors = await qAll.Where(t => t.Device != "Bot").Select(t => t.VisitorKey).Distinct().CountAsync();

        // "Today" in West Africa Time (UTC+1): WAT-midnight is 23:00 UTC the day before.
        var todayStartUtc = DateTime.UtcNow.AddHours(1).Date.AddHours(-1);
        var today = _db.TrafficHits.AsNoTracking().Where(t => t.CreatedAt >= todayStartUtc);
        if (humanOnly) today = today.Where(t => t.Device != "Bot");
        vm.TodayViews    = await today.CountAsync();
        vm.TodayVisitors = await today.Select(t => t.VisitorKey).Distinct().CountAsync();

        // ── Conversions: paid online orders in the same window (a paid order = a converted visit).
        // Dated by payment time, UTC-day so the buckets line up with the traffic chart. Fetch PaidAt+Total
        // and aggregate in memory (the window holds at most a few thousand online orders) to avoid any
        // provider-specific DateTime.Date translation quirk on the nullable PaidAt.
        var paidOnline = await _db.Orders.AsNoTracking()
            .Where(o => o.Channel == SterlingLams.Web.Models.Domain.OrderChannel.Online
                        && o.PaidAt != null && o.PaidAt >= start)
            .Select(o => new { o.PaidAt, o.Total })
            .ToListAsync();
        vm.TotalOrders  = paidOnline.Count;
        vm.TotalRevenue = paidOnline.Sum(o => o.Total);
        vm.TodayOrders  = paidOnline.Count(o => o.PaidAt >= todayStartUtc);
        vm.TodayRevenue = paidOnline.Where(o => o.PaidAt >= todayStartUtc).Sum(o => o.Total);
        var dOrders = paidOnline.GroupBy(o => o.PaidAt!.Value.Date).ToDictionary(g => g.Key, g => g.Count());

        // Daily views + unique visitors (grouped in SQL by calendar day).
        var dViews = (await q.GroupBy(t => t.CreatedAt.Date)
                .Select(g => new { g.Key, C = g.Count() }).ToListAsync())
            .ToDictionary(x => x.Key, x => x.C);
        var dVisitors = (await q.Select(t => new { t.CreatedAt.Date, t.VisitorKey }).Distinct()
                .GroupBy(x => x.Date).Select(g => new { g.Key, C = g.Count() }).ToListAsync())
            .ToDictionary(x => x.Key, x => x.C);
        for (var i = 0; i < days; i++)
        {
            var d = start.AddDays(i);
            vm.DayLabels.Add(d.ToString("dd MMM"));
            vm.DayViews.Add(dViews.GetValueOrDefault(d));
            vm.DayVisitors.Add(dVisitors.GetValueOrDefault(d));
            vm.DayOrders.Add(dOrders.GetValueOrDefault(d));
        }

        vm.TopPages = (await q.GroupBy(t => t.Path).Select(g => new { g.Key, C = g.Count() })
                .OrderByDescending(x => x.C).Take(10).ToListAsync())
            .Select(x => new NameCount(x.Key, x.C)).ToList();

        vm.TopReferrers = (await q.Where(t => t.RefererHost != null).GroupBy(t => t.RefererHost!)
                .Select(g => new { g.Key, C = g.Count() }).OrderByDescending(x => x.C).Take(8).ToListAsync())
            .Select(x => new NameCount(x.Key, x.C)).ToList();

        vm.Devices = (await q.GroupBy(t => t.Device).Select(g => new { g.Key, C = g.Count() })
                .OrderByDescending(x => x.C).ToListAsync())
            .Select(x => new NameCount(x.Key, x.C)).ToList();

        // ── Meta events sent (server-side CAPI): a send-side confirmation that our server is firing
        // AddToCart / InitiateCheckout / Purchase to Meta. This is OUR send log, not Meta's attribution
        // (that lives in Events Manager → Overview). Counts are the WAT-day aggregates we batch-write.
        var startDay = DateOnly.FromDateTime(start);
        var watToday = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(1));
        var metaRows = await _db.MetaEventDaily.AsNoTracking()
            .Where(m => m.Day >= startDay)
            .Select(m => new { m.Day, m.EventName, m.Sent, m.Accepted })
            .ToListAsync();
        vm.MetaEvents = metaRows.GroupBy(m => m.EventName)
            .Select(g => new MetaEventStat(g.Key, g.Sum(x => x.Sent), g.Sum(x => x.Accepted)))
            .OrderByDescending(s => s.Sent).ToList();
        vm.MetaTotalSent     = metaRows.Sum(m => m.Sent);
        vm.MetaTotalAccepted = metaRows.Sum(m => m.Accepted);
        vm.MetaTodaySent     = metaRows.Where(m => m.Day == watToday).Sum(m => m.Sent);
        vm.MetaCapiEnabled   = await _settings.GetBoolAsync("meta.capi_enabled", false);

        // Cloudflare edge/security analytics (requests, threats blocked, countries) — best effort.
        vm.Cf = await _cf.GetAsync(days);
        vm.CfZoneId  = await _settings.GetAsync("cloudflare.zone_id", "");
        vm.CfTokenSet = (await _settings.GetAsync("cloudflare.api_token", "")).Length > 0;

        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCloudflare(string? apiToken, string? zoneId)
    {
        var updates = new Dictionary<string, string>
        {
            ["cloudflare.zone_id"] = (zoneId ?? "").Trim(),
        };
        // Only overwrite the token when a new one is entered (blank = keep the existing one).
        if (!string.IsNullOrWhiteSpace(apiToken))
            updates["cloudflare.api_token"] = _secrets.Protect(apiToken.Trim());

        await _settings.SaveManyAsync(updates);
        await LogAsync("Update", "Setting", null, "Updated Cloudflare analytics connection");
        TempData["Success"] = "Cloudflare connection saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DisconnectCloudflare()
    {
        await _settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["cloudflare.api_token"] = "",
            ["cloudflare.zone_id"] = "",
        });
        await LogAsync("Update", "Setting", null, "Disconnected Cloudflare analytics");
        TempData["Success"] = "Cloudflare disconnected.";
        return RedirectToAction(nameof(Index));
    }

    public record NameCount(string Name, int Count);
    public record MetaEventStat(string EventName, int Sent, int Accepted);

    public class TrafficViewModel
    {
        public int Days { get; set; } = 30;
        public bool HumanOnly { get; set; }
        public int HumanViews { get; set; }
        public int BotViews { get; set; }
        public int TotalViews { get; set; }
        public int TotalVisitors { get; set; }
        public int HumanVisitors { get; set; }
        public int TodayViews { get; set; }
        public int TodayVisitors { get; set; }
        // Conversions = paid online orders in the window.
        public int TotalOrders { get; set; }
        public decimal TotalRevenue { get; set; }
        public int TodayOrders { get; set; }
        public decimal TodayRevenue { get; set; }
        public decimal ConversionRate => HumanVisitors > 0 ? (decimal)TotalOrders / HumanVisitors * 100m : 0m;
        public decimal AvgOrderValue => TotalOrders > 0 ? TotalRevenue / TotalOrders : 0m;
        public List<string> DayLabels { get; } = new();
        public List<int> DayViews { get; } = new();
        public List<int> DayVisitors { get; } = new();
        public List<int> DayOrders { get; } = new();
        public List<NameCount> TopPages { get; set; } = new();
        public List<NameCount> TopReferrers { get; set; } = new();
        public List<NameCount> Devices { get; set; } = new();
        public CloudflareResult? Cf { get; set; }
        public string CfZoneId { get; set; } = "";
        public bool CfTokenSet { get; set; }
        // Meta CAPI send-side telemetry (our server → Meta), distinct from Meta's attribution.
        public List<MetaEventStat> MetaEvents { get; set; } = new();
        public int MetaTotalSent { get; set; }
        public int MetaTotalAccepted { get; set; }
        public int MetaTodaySent { get; set; }
        public bool MetaCapiEnabled { get; set; }
    }
}
