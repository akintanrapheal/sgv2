using Microsoft.AspNetCore.Mvc;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services.Marketing;

namespace SterlingLams.Web.Areas.Marketing.Controllers;

/// <summary>Read-only overview of the built-in segments + their live sizes. Campaigns target one
/// of these at send time.</summary>
public class AudiencesController : MarketingAreaController
{
    private readonly IMarketingService _marketing;
    public AudiencesController(IMarketingService marketing) => _marketing = marketing;

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Audiences";

        var segments = new (string Name, string Desc, Campaign Probe)[]
        {
            ("All customers", "Everyone who has placed a paid order.", new Campaign { Audience = CampaignAudience.AllCustomers }),
            ("Newsletter subscribers", "Signed up via the storefront newsletter.", new Campaign { Audience = CampaignAudience.NewsletterSubscribers }),
            ("Recent buyers (90d)", "Bought within the last 90 days.", new Campaign { Audience = CampaignAudience.RecentBuyers, AudienceDays = 90 }),
            ("Lapsed customers (90d+)", "Bought before, but nothing in 90 days.", new Campaign { Audience = CampaignAudience.LapsedCustomers, AudienceDays = 90 }),
            ("Never ordered", "Registered customers with no orders yet.", new Campaign { Audience = CampaignAudience.NeverOrdered }),
            ("Lagos customers", "Most recent delivery in Lagos.", new Campaign { Audience = CampaignAudience.ByState, AudienceState = "Lagos" }),
        };

        var rows = new List<(string Name, string Desc, int Count)>();
        foreach (var s in segments)
            rows.Add((s.Name, s.Desc, await _marketing.EstimateCountAsync(s.Probe)));

        return View(rows);
    }
}
