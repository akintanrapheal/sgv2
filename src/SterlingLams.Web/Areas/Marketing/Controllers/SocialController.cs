using System.Security.Claims;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services.Social;

namespace SterlingLams.Web.Areas.Marketing.Controllers;

/// <summary>Social content calendar — compose + schedule posts for Instagram/Facebook/TikTok.
/// Publishing is dormant until accounts are connected (see ISocialPublisher.IsEnabled).</summary>
public class SocialController : MarketingAreaController
{
    private readonly ApplicationDbContext _db;
    private readonly ISocialPublisher _publisher;
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;

    private static readonly HashSet<string> _allowedExt = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".gif" };

    public SocialController(ApplicationDbContext db, ISocialPublisher publisher,
        IWebHostEnvironment env, IConfiguration config)
    {
        _db = db;
        _publisher = publisher;
        _env = env;
        _config = config;
    }

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Social";
        ViewBag.PublishingEnabled = _publisher.IsEnabled;
        var posts = await _db.SocialPosts.AsNoTracking()
            .OrderByDescending(p => p.ScheduledAt ?? p.CreatedAt)
            .Take(100).ToListAsync();
        return View(posts);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(int id, string content, string? imageUrl, SocialChannel[]? channels, DateTime? scheduledAt)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            TempData["Error"] = "Write something to post.";
            return RedirectToAction(nameof(Index));
        }

        var isNew = id == 0;
        var p = isNew ? new SocialPost() : await _db.SocialPosts.FindAsync(id);
        if (p == null) return NotFound();
        // Sent/failed posts are history — don't edit.
        if (!isNew && p.Status == SocialPostStatus.Published) return RedirectToAction(nameof(Index));

        p.Content = content.Trim();
        p.ImageUrl = string.IsNullOrWhiteSpace(imageUrl) ? null : imageUrl.Trim();
        var flags = SocialChannel.None;
        foreach (var c in channels ?? Array.Empty<SocialChannel>()) flags |= c;
        p.Channels = flags;
        p.ScheduledAt = scheduledAt.HasValue ? DateTime.SpecifyKind(scheduledAt.Value, DateTimeKind.Utc) : null;
        p.Status = p.ScheduledAt.HasValue ? SocialPostStatus.Scheduled : SocialPostStatus.Draft;
        p.Error = null;
        p.UpdatedAt = DateTime.UtcNow;
        if (isNew) { p.CreatedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier); _db.SocialPosts.Add(p); }
        await _db.SaveChangesAsync();
        await LogAsync(isNew ? "Create" : "Update", "SocialPost", p.Id.ToString(), $"{(isNew ? "Scheduled" : "Updated")} social post");
        TempData["Success"] = p.Status == SocialPostStatus.Scheduled ? "Post scheduled." : "Draft saved.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Uploads a post image so the team doesn't need an external URL. Stores to Cloudinary
    /// (persistent + CDN) when configured — required on Render where local disk is wiped on redeploy —
    /// and falls back to local disk in dev. Returns { url } for the compose form to fill.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(11 * 1024 * 1024)]
    public async Task<IActionResult> Upload(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file provided." });
        if (file.Length > 10 * 1024 * 1024)
            return BadRequest(new { error = "File too large. Maximum 10 MB." });

        var ext = Path.GetExtension(file.FileName);
        if (!_allowedExt.Contains(ext))
            return BadRequest(new { error = "Invalid file type. Allowed: JPG, PNG, WEBP, GIF." });

        var cloudName = _config["Cloudinary:CloudName"];
        var apiKey    = _config["Cloudinary:ApiKey"];
        var apiSecret = _config["Cloudinary:ApiSecret"];
        if (!string.IsNullOrWhiteSpace(cloudName) && !string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(apiSecret))
        {
            var cloudinary = new Cloudinary(new Account(cloudName, apiKey, apiSecret)) { Api = { Secure = true } };
            await using var s = file.OpenReadStream();
            var result = await cloudinary.UploadAsync(new ImageUploadParams
            {
                File = new FileDescription(file.FileName, s),
                Folder = "sterlinglams/social",
                PublicId = Guid.NewGuid().ToString("N"),
                UniqueFilename = false,
                Overwrite = false
            });
            if (result.StatusCode != System.Net.HttpStatusCode.OK || result.SecureUrl == null)
                return BadRequest(new { error = "Image upload failed. Please try again." });
            return Ok(new { url = result.SecureUrl.ToString() });
        }

        // Dev fallback: local disk (NOT persistent on Render — configure Cloudinary for production).
        var dir = Path.Combine(_env.WebRootPath, "uploads", "social");
        Directory.CreateDirectory(dir);
        var fileName = $"{Guid.NewGuid():N}{ext.ToLowerInvariant()}";
        await using var stream = System.IO.File.Create(Path.Combine(dir, fileName));
        await file.CopyToAsync(stream);
        return Ok(new { url = $"/uploads/social/{fileName}" });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var p = await _db.SocialPosts.FindAsync(id);
        if (p != null)
        {
            _db.SocialPosts.Remove(p);
            await _db.SaveChangesAsync();
            await LogAsync("Delete", "SocialPost", id.ToString(), "Deleted social post");
            TempData["Success"] = "Post deleted.";
        }
        return RedirectToAction(nameof(Index));
    }
}
