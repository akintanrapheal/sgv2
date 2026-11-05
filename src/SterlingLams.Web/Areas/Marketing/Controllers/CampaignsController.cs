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
    private readonly IMarketingAttributionService _attribution;
    private readonly IImageStorageService _imageStorage;

    public CampaignsController(ApplicationDbContext db, IMarketingService marketing,
        IMarketingAttributionService attribution, IImageStorageService imageStorage)
    {
        _db = db;
        _marketing = marketing;
        _attribution = attribution;
        _imageStorage = imageStorage;
    }

    public async Task<IActionResult> Index(int page = 1)
    {
        const int pageSize = 30;
        ViewData["Title"] = "Campaigns";
        if (page < 1) page = 1;
        var total = await _db.Campaigns.CountAsync();
        ViewBag.Page = page;
        ViewBag.TotalPages = (int)Math.Ceiling(total / (double)pageSize);
        var campaigns = await _db.Campaigns.AsNoTracking()
            .OrderByDescending(c => c.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        // Attributed revenue/orders for the campaigns on this page (last-touch, from the earliest
        // send shown to now — so a campaign's lifetime conversions line up with its Detail page).
        var revenue = new Dictionary<int, (decimal Revenue, int Orders)>();
        var earliest = campaigns.Where(c => c.SentAt != null).Select(c => c.SentAt!.Value)
            .DefaultIfEmpty().Min();
        if (earliest != default)
        {
            var attr = await _attribution.ComputeAsync(earliest, DateTime.UtcNow);
            foreach (var c in campaigns.Where(c => c.SentAt != null))
                revenue[c.Id] = attr.ForCampaign(c.Id);
        }
        ViewBag.Revenue = revenue;
        return View(campaigns);
    }

    public async Task<IActionResult> Create()
    {
        ViewData["Title"] = "New Campaign";
        await LoadSegmentsAsync();
        return View("Edit", new Campaign());
    }

    public async Task<IActionResult> Edit(int id)
    {
        var c = await _db.Campaigns.FindAsync(id);
        if (c == null) return NotFound();
        if (c.Status != CampaignStatus.Draft)
            return RedirectToAction(nameof(Detail), new { id });  // sent/sending campaigns are read-only
        ViewData["Title"] = "Edit Campaign";
        await LoadSegmentsAsync();
        return View(c);
    }

    private async Task LoadSegmentsAsync() =>
        ViewBag.Segments = await _db.Segments.AsNoTracking().OrderBy(s => s.Name).ToListAsync();

    // ─────────────────────── Image library ("Asset Manager") ───────────────────────
    // The email builder's image blocks upload here and pick from the reusable library below,
    // so a photo used once can be reused in any later campaign without re-uploading.

    /// <summary>Uploads one image into the shared store and records it in the library.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(12 * 1024 * 1024)]
    public async Task<IActionResult> UploadAsset(IFormFile file)
    {
        var invalid = ImageUploadRules.Validate(file);
        if (invalid != null) return BadRequest(new { error = invalid });

        var url = await _imageStorage.UploadAsync(file, "marketing");
        if (url == null) return BadRequest(new { error = "Upload failed. Please try again." });

        var asset = new MarketingAsset
        {
            Url = url,
            FileName = Path.GetFileName(file.FileName),
            SizeBytes = file.Length,
            ContentType = file.ContentType,
            CreatedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier),
        };
        _db.MarketingAssets.Add(asset);
        await _db.SaveChangesAsync();
        return Ok(new { id = asset.Id, url = asset.Url, fileName = asset.FileName });
    }

    /// <summary>Lists library images, newest first, for the Asset Manager grid ("Load more").</summary>
    [HttpGet]
    public async Task<IActionResult> Assets(int skip = 0, int take = 40)
    {
        if (skip < 0) skip = 0;
        take = Math.Clamp(take, 1, 100);
        var total = await _db.MarketingAssets.CountAsync();
        var items = await _db.MarketingAssets.AsNoTracking()
            .OrderByDescending(a => a.CreatedAt)
            .Skip(skip).Take(take)
            .Select(a => new { a.Id, a.Url, a.FileName })
            .ToListAsync();
        return Ok(new { total, items });
    }

    /// <summary>Removes an image from the library (does not delete the stored file).</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteAsset(int id)
    {
        var asset = await _db.MarketingAssets.FindAsync(id);
        if (asset == null) return NotFound();
        _db.MarketingAssets.Remove(asset);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Campaign vm)
    {
        if (string.IsNullOrWhiteSpace(vm.Name) || string.IsNullOrWhiteSpace(vm.Subject))
        {
            TempData["Error"] = "Name and subject are required.";
            await LoadSegmentsAsync();
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
        // Block composer: render the JSON layout to trusted email HTML (the renderer encodes/sanitises
        // each field itself, so it bypasses the blanket SanitizeRich that would strip images/buttons).
        // Plain editor: sanitise the raw HTML as before.
        if (!string.IsNullOrWhiteSpace(vm.LayoutJson))
        {
            c.LayoutJson = vm.LayoutJson;
            c.BodyHtml = CampaignLayoutRenderer.Render(vm.LayoutJson);
        }
        else
        {
            c.LayoutJson = null;
            c.BodyHtml = ProductHtml.SanitizeRich(vm.BodyHtml ?? "");
        }
        c.SegmentId = vm.SegmentId;
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
            .Where(r => r.CampaignId == id).OrderByDescending(r => r.Id).Take(300).ToListAsync();

        // Aggregate report tiles (over ALL recipients, not just the listed 300) for sent campaigns.
        if (c.Status != CampaignStatus.Draft)
        {
            var recips = _db.CampaignRecipients.AsNoTracking().Where(r => r.CampaignId == id);
            ViewBag.Delivered = await recips.CountAsync(r => r.Status == CampaignRecipientStatus.Sent);
            ViewBag.Opened = await recips.CountAsync(r => r.OpenedAt != null);
            ViewBag.Clicked = await recips.CountAsync(r => r.ClickedAt != null);
            ViewBag.FailedTile = await recips.CountAsync(r => r.Status == CampaignRecipientStatus.Failed);
            ViewBag.SkippedTile = await recips.CountAsync(r => r.Status == CampaignRecipientStatus.Skipped);
            // Unsubscribed = recipients whose address later landed on the suppression list.
            ViewBag.Unsubscribed = await recips
                .CountAsync(r => _db.MarketingSuppressions.Any(s => s.Email == r.Email.ToLower()));
        }

        // Revenue attributed to this campaign (last-touch, from its send date to now).
        if (c.SentAt is DateTime sentAt)
        {
            var attr = await _attribution.ComputeAsync(sentAt, DateTime.UtcNow);
            var (rev, orders) = attr.ForCampaign(id);
            ViewBag.AttributedRevenue = rev;
            ViewBag.AttributedOrders = orders;
            ViewBag.AttributionWindow = attr.WindowDays;
        }
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
