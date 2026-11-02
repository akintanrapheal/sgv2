using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services.Marketing;

namespace SterlingLams.Web.Areas.Marketing.Controllers;

/// <summary>Conversions &amp; revenue analytics — surfaces the email attribution engine
/// (<see cref="IMarketingAttributionService"/>) over a chosen date range: totals, revenue over
/// time, source split, and the per-campaign / per-automation performance breakdown. Read-only.</summary>
public class AnalyticsController : MarketingAreaController
{
    private readonly ApplicationDbContext _db;
    private readonly IMarketingAttributionService _attribution;
    public AnalyticsController(ApplicationDbContext db, IMarketingAttributionService attribution)
    {
        _db = db;
        _attribution = attribution;
    }

    public async Task<IActionResult> Index(int days = 30)
    {
        ViewData["Title"] = "Analytics";
        // Clamp to the presets the UI offers so the chart series stays a sane length.
        if (days != 7 && days != 30 && days != 90 && days != 365) days = 30;
        ViewBag.Days = days;

        var now = DateTime.UtcNow;
        var from = now.AddDays(-days);
        var attr = await _attribution.ComputeAsync(from, now);
        ViewBag.Attribution = attr;

        // Recipient counts for the campaigns that earned revenue, so we can show a conversion rate
        // (orders ÷ recipients). One query over just the ids in the breakdown.
        var campIds = attr.Campaigns.Select(c => c.Id!.Value).ToList();
        ViewBag.Recipients = campIds.Count == 0
            ? new Dictionary<int, int>()
            : await _db.Campaigns.Where(c => campIds.Contains(c.Id))
                .Select(c => new { c.Id, c.RecipientCount })
                .ToDictionaryAsync(c => c.Id, c => c.RecipientCount);

        // All-time email engagement (opens/clicks are tracked on campaigns via the self-hosted
        // pixel + click redirect; automations track sends only).
        ViewBag.CampSent = await _db.Campaigns.SumAsync(c => (int?)c.SentCount) ?? 0;
        ViewBag.CampOpens = await _db.Campaigns.SumAsync(c => (int?)c.OpenCount) ?? 0;
        ViewBag.CampClicks = await _db.Campaigns.SumAsync(c => (int?)c.ClickCount) ?? 0;
        ViewBag.AutoSent = await _db.Automations.SumAsync(a => (int?)a.SentCount) ?? 0;
        return View();
    }
}
