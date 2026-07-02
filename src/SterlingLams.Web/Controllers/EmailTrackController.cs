using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services.Marketing;

namespace SterlingLams.Web.Controllers;

/// <summary>
/// Self-hosted email open/click tracking. A 1×1 pixel records opens; wrapped links record a click
/// then redirect on. Both keyed to a data-protected campaign-recipient token, counted once each.
/// </summary>
[AllowAnonymous]
public class EmailTrackController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IMarketingService _marketing;
    public EmailTrackController(ApplicationDbContext db, IMarketingService marketing)
    {
        _db = db;
        _marketing = marketing;
    }

    // 1×1 transparent GIF.
    private static readonly byte[] Pixel = Convert.FromBase64String(
        "R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

    [HttpGet("/e/o/{token}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Open(string token)
    {
        await RecordOpenAsync(token);
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        return File(Pixel, "image/gif");
    }

    [HttpGet("/e/c/{token}")]
    public async Task<IActionResult> Click(string token, string? u)
    {
        var rid = _marketing.ReadTrackToken(token);
        if (rid is int id)
        {
            var r = await _db.CampaignRecipients.FirstOrDefaultAsync(x => x.Id == id);
            if (r != null)
            {
                var now = DateTime.UtcNow;
                var changed = false;
                if (r.ClickedAt == null) { r.ClickedAt = now; await BumpAsync(r.CampaignId, click: true); changed = true; }
                if (r.OpenedAt == null) { r.OpenedAt = now; await BumpAsync(r.CampaignId, click: false); changed = true; }
                if (changed) await _db.SaveChangesAsync();
            }
        }
        // Only follow absolute http(s) targets; anything else goes home.
        if (!string.IsNullOrWhiteSpace(u) && Uri.TryCreate(u, UriKind.Absolute, out var dest)
            && (dest.Scheme == Uri.UriSchemeHttp || dest.Scheme == Uri.UriSchemeHttps))
            return Redirect(u);
        return RedirectToAction("Index", "Home");
    }

    private async Task RecordOpenAsync(string token)
    {
        var rid = _marketing.ReadTrackToken(token);
        if (rid is not int id) return;
        var r = await _db.CampaignRecipients.FirstOrDefaultAsync(x => x.Id == id);
        if (r == null || r.OpenedAt != null) return;
        r.OpenedAt = DateTime.UtcNow;
        await BumpAsync(r.CampaignId, click: false);
        await _db.SaveChangesAsync();
    }

    private async Task BumpAsync(int campaignId, bool click)
    {
        var c = await _db.Campaigns.FirstOrDefaultAsync(x => x.Id == campaignId);
        if (c == null) return;
        if (click) c.ClickCount++; else c.OpenCount++;
    }
}
