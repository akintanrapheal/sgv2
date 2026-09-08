using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.Areas.Admin.Controllers;

/// <summary>Admin → Traffic: storefront page-view analytics from our own request log (TrafficHits).
/// Owner/super-admin only (Section == null), matching Users/Roles/Integrations.</summary>
public class TrafficController : AdminBaseController
{
    protected override string? Section => null;

    private readonly ApplicationDbContext _db;
    public TrafficController(ApplicationDbContext db) => _db = db;

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

        // "Today" in West Africa Time (UTC+1): WAT-midnight is 23:00 UTC the day before.
        var todayStartUtc = DateTime.UtcNow.AddHours(1).Date.AddHours(-1);
        var today = _db.TrafficHits.AsNoTracking().Where(t => t.CreatedAt >= todayStartUtc);
        if (humanOnly) today = today.Where(t => t.Device != "Bot");
        vm.TodayViews    = await today.CountAsync();
        vm.TodayVisitors = await today.Select(t => t.VisitorKey).Distinct().CountAsync();

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

        return View(vm);
    }

    public record NameCount(string Name, int Count);

    public class TrafficViewModel
    {
        public int Days { get; set; } = 30;
        public bool HumanOnly { get; set; }
        public int HumanViews { get; set; }
        public int BotViews { get; set; }
        public int TotalViews { get; set; }
        public int TotalVisitors { get; set; }
        public int TodayViews { get; set; }
        public int TodayVisitors { get; set; }
        public List<string> DayLabels { get; } = new();
        public List<int> DayViews { get; } = new();
        public List<int> DayVisitors { get; } = new();
        public List<NameCount> TopPages { get; set; } = new();
        public List<NameCount> TopReferrers { get; set; } = new();
        public List<NameCount> Devices { get; set; } = new();
    }
}
