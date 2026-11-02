using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services.Marketing;

/// <summary>Revenue attributed to one marketing source (a campaign, an automation, or cart recovery).</summary>
public record AttributedSource(string Type, int? Id, string Name, decimal Revenue, int Orders)
{
    public decimal Aov => Orders > 0 ? Math.Round(Revenue / Orders, 0) : 0m;
}

/// <summary>One day's attributed revenue, split by source. Used to draw the revenue-over-time
/// chart on the Analytics page; days with no attributed order are zero-filled so the series is
/// contiguous.</summary>
public record DailyRevenue(DateTime Day, decimal Campaign, decimal Automation, decimal Cart)
{
    public decimal Total => Campaign + Automation + Cart;
}

public class AttributionResult
{
    public int WindowDays { get; set; }
    public decimal TotalRevenue { get; set; }
    public int TotalOrders { get; set; }
    public decimal CampaignRevenue { get; set; }
    public decimal AutomationRevenue { get; set; }
    public decimal CartRevenue { get; set; }
    public List<AttributedSource> Campaigns { get; set; } = new();
    public List<AttributedSource> Automations { get; set; } = new();
    /// <summary>Contiguous day-by-day attributed revenue across the requested range (local days).</summary>
    public List<DailyRevenue> Daily { get; set; } = new();

    public decimal Aov => TotalOrders > 0 ? Math.Round(TotalRevenue / TotalOrders, 0) : 0m;

    /// <summary>Revenue + order count credited to a single campaign (last-touch, so it already
    /// excludes orders a later email won).</summary>
    public (decimal Revenue, int Orders) ForCampaign(int id)
    {
        var s = Campaigns.FirstOrDefault(x => x.Id == id);
        return s == null ? (0m, 0) : (s.Revenue, s.Orders);
    }

    /// <summary>Revenue + order count credited to a single automation (last-touch).</summary>
    public (decimal Revenue, int Orders) ForAutomation(int id)
    {
        var s = Automations.FirstOrDefault(x => x.Id == id);
        return s == null ? (0m, 0) : (s.Revenue, s.Orders);
    }
}

public interface IMarketingAttributionService
{
    /// <summary>Last-touch email revenue attribution over paid online orders in [from, to): each order
    /// is credited to the most recent marketing email sent to that buyer within the attribution
    /// window before payment (so no order is counted twice). POS sales are excluded.</summary>
    Task<AttributionResult> ComputeAsync(DateTime from, DateTime to, int? windowDays = null);
}

public class MarketingAttributionService : IMarketingAttributionService
{
    private readonly ApplicationDbContext _db;
    private readonly ISettingsService _settings;

    public MarketingAttributionService(ApplicationDbContext db, ISettingsService settings)
    {
        _db = db;
        _settings = settings;
    }

    public async Task<AttributionResult> ComputeAsync(DateTime from, DateTime to, int? windowDays = null)
    {
        var window = windowDays ?? await _settings.GetIntAsync("marketing.attribution_window_days", 7);
        if (window < 1) window = 7;
        var sendFrom = from.AddDays(-window);

        var result = new AttributionResult { WindowDays = window };

        // Paid online orders in range, with the buyer's email (guest shells carry the guest's email).
        var orders = await _db.Orders
            .Where(o => o.IsPaid && o.PaidAt != null && o.PaidAt >= from && o.PaidAt < to
                        && o.Channel == OrderChannel.Online)
            .Join(_db.Users, o => o.UserId, u => u.Id, (o, u) => new { u.Email, o.Total, PaidAt = o.PaidAt!.Value })
            .Where(x => x.Email != null)
            .ToListAsync();
        if (orders.Count == 0) return result;

        // Email send events that could have influenced those orders.
        var campSends = await _db.CampaignRecipients
            .Where(r => r.Status == CampaignRecipientStatus.Sent && r.SentAt != null && r.SentAt >= sendFrom && r.SentAt < to)
            .Select(r => new { r.Email, At = r.SentAt!.Value, r.CampaignId }).ToListAsync();
        var autoSends = await _db.AutomationRuns
            .Where(r => r.Status == AutomationRunStatus.Sent && r.SentAt != null && r.SentAt >= sendFrom && r.SentAt < to)
            .Select(r => new { r.Email, At = r.SentAt!.Value, r.AutomationId }).ToListAsync();
        var cartSends = await _db.AbandonedCarts
            .Where(c => c.EmailedAt != null && c.EmailedAt >= sendFrom && c.EmailedAt < to)
            .Select(c => new { c.Email, At = c.EmailedAt!.Value }).ToListAsync();

        // Group send events by email so each order only scans its own sends.
        var byEmail = new Dictionary<string, List<(DateTime At, string Type, int? Id)>>(StringComparer.OrdinalIgnoreCase);
        void Add(string? email, DateTime at, string type, int? id)
        {
            if (string.IsNullOrWhiteSpace(email)) return;
            if (!byEmail.TryGetValue(email, out var list)) { list = new(); byEmail[email] = list; }
            list.Add((at, type, id));
        }
        foreach (var s in campSends) Add(s.Email, s.At, "Campaign", s.CampaignId);
        foreach (var s in autoSends) Add(s.Email, s.At, "Automation", s.AutomationId);
        foreach (var s in cartSends) Add(s.Email, s.At, "Cart", null);

        var campAgg = new Dictionary<int, (decimal rev, int n)>();
        var autoAgg = new Dictionary<int, (decimal rev, int n)>();
        // Daily buckets (local day → per-source revenue) for the revenue-over-time chart.
        var dayAgg = new Dictionary<DateTime, (decimal camp, decimal auto, decimal cart)>();

        foreach (var o in orders)
        {
            if (o.Email == null || !byEmail.TryGetValue(o.Email, out var sends)) continue;
            var lower = o.PaidAt.AddDays(-window);

            (DateTime At, string Type, int? Id)? best = null;
            foreach (var s in sends)
                if (s.At <= o.PaidAt && s.At > lower && (best == null || s.At > best.Value.At))
                    best = s;
            if (best == null) continue;

            result.TotalRevenue += o.Total;
            result.TotalOrders++;
            var day = o.PaidAt.ToLocalTime().Date;
            var d = dayAgg.GetValueOrDefault(day);
            switch (best.Value.Type)
            {
                case "Campaign":
                    result.CampaignRevenue += o.Total;
                    var ca = campAgg.GetValueOrDefault(best.Value.Id!.Value);
                    campAgg[best.Value.Id!.Value] = (ca.rev + o.Total, ca.n + 1);
                    dayAgg[day] = (d.camp + o.Total, d.auto, d.cart);
                    break;
                case "Automation":
                    result.AutomationRevenue += o.Total;
                    var aa = autoAgg.GetValueOrDefault(best.Value.Id!.Value);
                    autoAgg[best.Value.Id!.Value] = (aa.rev + o.Total, aa.n + 1);
                    dayAgg[day] = (d.camp, d.auto + o.Total, d.cart);
                    break;
                default:
                    result.CartRevenue += o.Total;
                    dayAgg[day] = (d.camp, d.auto, d.cart + o.Total);
                    break;
            }
        }

        // Zero-fill a contiguous local-day series across [from, to) so the chart has no gaps.
        var startDay = from.ToLocalTime().Date;
        var endDay = to.AddTicks(-1).ToLocalTime().Date;
        for (var day = startDay; day <= endDay; day = day.AddDays(1))
        {
            var d = dayAgg.GetValueOrDefault(day);
            result.Daily.Add(new DailyRevenue(day, d.camp, d.auto, d.cart));
        }

        // Resolve names for the breakdown.
        var campNames = await _db.Campaigns.Where(c => campAgg.Keys.Contains(c.Id))
            .Select(c => new { c.Id, c.Name }).ToDictionaryAsync(c => c.Id, c => c.Name);
        result.Campaigns = campAgg
            .Select(kv => new AttributedSource("Campaign", kv.Key,
                campNames.GetValueOrDefault(kv.Key, $"#{kv.Key}"), kv.Value.rev, kv.Value.n))
            .OrderByDescending(x => x.Revenue).ToList();

        var autoNames = await _db.Automations.Where(a => autoAgg.Keys.Contains(a.Id))
            .Select(a => new { a.Id, a.Name }).ToDictionaryAsync(a => a.Id, a => a.Name);
        result.Automations = autoAgg
            .Select(kv => new AttributedSource("Automation", kv.Key,
                autoNames.GetValueOrDefault(kv.Key, $"#{kv.Key}"), kv.Value.rev, kv.Value.n))
            .OrderByDescending(x => x.Revenue).ToList();

        return result;
    }
}
