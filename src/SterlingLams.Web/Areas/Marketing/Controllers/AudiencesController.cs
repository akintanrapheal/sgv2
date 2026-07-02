using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services.Marketing;

namespace SterlingLams.Web.Areas.Marketing.Controllers;

/// <summary>Built-in segment sizes + saved custom segments (reusable audiences for campaigns).</summary>
public class AudiencesController : MarketingAreaController
{
    private readonly IMarketingService _marketing;
    private readonly ApplicationDbContext _db;
    public AudiencesController(IMarketingService marketing, ApplicationDbContext db)
    {
        _marketing = marketing;
        _db = db;
    }

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Audiences";

        var builtins = new (string Name, string Desc, Campaign Probe)[]
        {
            ("All customers", "Everyone who has placed a paid order.", new Campaign { Audience = CampaignAudience.AllCustomers }),
            ("Newsletter subscribers", "Signed up via the storefront newsletter.", new Campaign { Audience = CampaignAudience.NewsletterSubscribers }),
            ("Recent buyers (90d)", "Bought within the last 90 days.", new Campaign { Audience = CampaignAudience.RecentBuyers, AudienceDays = 90 }),
            ("Lapsed customers (90d+)", "Bought before, but nothing in 90 days.", new Campaign { Audience = CampaignAudience.LapsedCustomers, AudienceDays = 90 }),
            ("Never ordered", "Registered customers with no orders yet.", new Campaign { Audience = CampaignAudience.NeverOrdered }),
            ("Lagos customers", "Most recent delivery in Lagos.", new Campaign { Audience = CampaignAudience.ByState, AudienceState = "Lagos" }),
        };
        var builtinRows = new List<(string Name, string Desc, int Count)>();
        foreach (var s in builtins)
            builtinRows.Add((s.Name, s.Desc, await _marketing.EstimateCountAsync(s.Probe)));
        ViewBag.Builtins = builtinRows;

        var saved = await _db.Segments.AsNoTracking().OrderByDescending(s => s.Id).ToListAsync();
        var savedRows = new List<(Segment Seg, int Count)>();
        foreach (var s in saved)
            savedRows.Add((s, await _marketing.EstimateCountAsync(ProbeFor(s))));
        return View(savedRows);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string name, string? description, CampaignAudience audience, int? days, decimal? minSpend, string? state)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["Error"] = "Give the segment a name.";
            return RedirectToAction(nameof(Index));
        }
        _db.Segments.Add(new Segment
        {
            Name = name.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            Audience = audience,
            Days = days,
            MinSpend = minSpend,
            State = string.IsNullOrWhiteSpace(state) ? null : state.Trim()
        });
        await _db.SaveChangesAsync();
        await LogAsync("Create", "Segment", null, $"Created segment '{name}'");
        TempData["Success"] = $"Segment '{name}' saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var seg = await _db.Segments.FindAsync(id);
        if (seg != null)
        {
            _db.Segments.Remove(seg); // campaigns' SegmentId is set null (FK)
            await _db.SaveChangesAsync();
            await LogAsync("Delete", "Segment", id.ToString(), $"Deleted segment '{seg.Name}'");
            TempData["Success"] = "Segment deleted.";
        }
        return RedirectToAction(nameof(Index));
    }

    private static Campaign ProbeFor(Segment s) => new()
    {
        Audience = s.Audience, AudienceDays = s.Days, AudienceMinSpend = s.MinSpend, AudienceState = s.State
    };
}
