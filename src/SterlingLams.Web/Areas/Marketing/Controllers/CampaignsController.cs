using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using SterlingLams.Web.Services.Marketing;

namespace SterlingLams.Web.Areas.Marketing.Controllers;

public class CampaignsController : MarketingAreaController
{
    private readonly ApplicationDbContext _db;
    private readonly IMarketingService _marketing;

    public CampaignsController(ApplicationDbContext db, IMarketingService marketing)
    {
        _db = db;
        _marketing = marketing;
    }

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Campaigns";
        var campaigns = await _db.Campaigns.AsNoTracking()
            .OrderByDescending(c => c.CreatedAt).ToListAsync();
        return View(campaigns);
    }

    public IActionResult Create()
    {
        ViewData["Title"] = "New Campaign";
        return View("Edit", new Campaign());
    }

    public async Task<IActionResult> Edit(int id)
    {
        var c = await _db.Campaigns.FindAsync(id);
        if (c == null) return NotFound();
        if (c.Status != CampaignStatus.Draft)
            return RedirectToAction(nameof(Detail), new { id });  // sent/sending campaigns are read-only
        ViewData["Title"] = "Edit Campaign";
        return View(c);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Campaign vm)
    {
        if (string.IsNullOrWhiteSpace(vm.Name) || string.IsNullOrWhiteSpace(vm.Subject))
        {
            TempData["Error"] = "Name and subject are required.";
            return View("Edit", vm);
        }

        var isNew = vm.Id == 0;
        var c = isNew ? new Campaign() : await _db.Campaigns.FindAsync(vm.Id);
        if (c == null) return NotFound();
        if (!isNew && c.Status != CampaignStatus.Draft)
        {
            TempData["Error"] = "Only draft campaigns can be edited.";
            return RedirectToAction(nameof(Detail), new { id = c.Id });
        }

        c.Name = vm.Name.Trim();
        c.Subject = vm.Subject.Trim();
        c.BodyHtml = ProductHtml.Sanitize(vm.BodyHtml ?? "");
        c.Audience = vm.Audience;
        c.AudienceDays = vm.AudienceDays;
        c.AudienceMinSpend = vm.AudienceMinSpend;
        c.AudienceState = string.IsNullOrWhiteSpace(vm.AudienceState) ? null : vm.AudienceState.Trim();
        c.CouponEnabled = vm.CouponEnabled;
        c.CouponType = vm.CouponType;
        c.CouponValue = vm.CouponValue;
        c.CouponExpiryDays = vm.CouponExpiryDays < 1 ? 14 : vm.CouponExpiryDays;
        c.CouponMinOrder = vm.CouponMinOrder;
        c.UpdatedAt = DateTime.UtcNow;
        if (isNew)
        {
            c.CreatedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            _db.Campaigns.Add(c);
        }
        await _db.SaveChangesAsync();
        await LogAsync(isNew ? "Create" : "Update", "Campaign", c.Id.ToString(), $"{(isNew ? "Created" : "Updated")} campaign '{c.Name}'");
        TempData["Success"] = "Campaign saved.";
        return RedirectToAction(nameof(Detail), new { id = c.Id });
    }

    /// <summary>AJAX — live audience size for the chosen segment (used on the editor).</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Count(Campaign vm)
    {
        var n = await _marketing.EstimateCountAsync(vm);
        return Json(new { count = n });
    }

    public async Task<IActionResult> Detail(int id)
    {
        var c = await _db.Campaigns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        ViewData["Title"] = c.Name;
        ViewBag.EstimatedCount = c.Status == CampaignStatus.Draft ? await _marketing.EstimateCountAsync(c) : c.RecipientCount;
        ViewBag.Recipients = await _db.CampaignRecipients.AsNoTracking()
            .Where(r => r.CampaignId == id).OrderByDescending(r => r.Id).Take(50).ToListAsync();
        return View(c);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Send(int id, DateTime? scheduledAt)
    {
        var c = await _db.Campaigns.FindAsync(id);
        if (c == null) return NotFound();
        if (c.Status != CampaignStatus.Draft)
        {
            TempData["Error"] = "This campaign has already been sent or scheduled.";
            return RedirectToAction(nameof(Detail), new { id });
        }
        if (string.IsNullOrWhiteSpace(c.Subject) || string.IsNullOrWhiteSpace(c.BodyHtml))
        {
            TempData["Error"] = "Add a subject and body before sending.";
            return RedirectToAction(nameof(Edit), new { id });
        }
        if (await _marketing.EstimateCountAsync(c) == 0)
        {
            TempData["Error"] = "This audience is currently empty — nothing to send.";
            return RedirectToAction(nameof(Detail), new { id });
        }

        c.ScheduledAt = scheduledAt.HasValue ? DateTime.SpecifyKind(scheduledAt.Value, DateTimeKind.Utc) : DateTime.UtcNow;
        c.Status = CampaignStatus.Scheduled;
        c.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await LogAsync("Send", "Campaign", c.Id.ToString(), $"{(scheduledAt.HasValue ? "Scheduled" : "Sent")} campaign '{c.Name}'");
        TempData["Success"] = scheduledAt.HasValue
            ? $"Campaign scheduled for {c.ScheduledAt:dd MMM yyyy HH:mm} UTC."
            : "Campaign queued — sending now.";
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id)
    {
        var c = await _db.Campaigns.FindAsync(id);
        if (c != null && c.Status == CampaignStatus.Scheduled)
        {
            c.Status = CampaignStatus.Draft;
            c.ScheduledAt = null;
            c.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            TempData["Success"] = "Schedule cancelled — back to draft.";
        }
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var c = await _db.Campaigns.FindAsync(id);
        if (c != null)
        {
            _db.Campaigns.Remove(c); // recipients cascade
            await _db.SaveChangesAsync();
            await LogAsync("Delete", "Campaign", id.ToString(), $"Deleted campaign '{c.Name}'");
            TempData["Success"] = "Campaign deleted.";
        }
        return RedirectToAction(nameof(Index));
    }
}
