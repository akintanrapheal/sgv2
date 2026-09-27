using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Infrastructure;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers;

/// <summary>
/// Magazine-style lookbook editor: full-width campaign photos with "shop the look" hotspots mapped to
/// products. Persisted as the <c>lookbook.campaigns</c> JSON setting (no schema/migration).
/// </summary>
public class LookbookController : AdminBaseController
{
    protected override string Section => "Lookbook";

    private readonly ApplicationDbContext _db;
    private readonly ISettingsService _settings;

    public LookbookController(ApplicationDbContext db, ISettingsService settings)
    {
        _db = db;
        _settings = settings;
    }

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Lookbook";
        var campaigns = LookbookData.Parse(await _settings.GetAsync("lookbook.campaigns", ""));

        // Resolve the products referenced by hotspots so the editor can label each dot.
        var ids = campaigns.SelectMany(c => c.Hotspots).Select(h => h.ProductId).Distinct().ToList();
        var products = await _db.Products
            .Where(p => ids.Contains(p.Id))
            .Select(p => new
            {
                p.Id,
                p.Name,
                Image = p.Images.OrderByDescending(i => i.IsPrimary).ThenBy(i => i.SortOrder).Select(i => i.Url).FirstOrDefault()
            })
            .ToListAsync();

        ViewData["ProductLabels"] = products.ToDictionary(
            p => p.Id,
            p => new { name = p.Name, image = Img.Cld(p.Image, 80, 80) ?? "/images/placeholder.jpg" });

        return View(campaigns);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save([FromBody] List<LookbookCampaign>? campaigns)
    {
        var clean = (campaigns ?? new())
            .Where(c => !string.IsNullOrWhiteSpace(c.Image))
            .Select(c => new LookbookCampaign
            {
                Image = c.Image.Trim(),
                Heading = string.IsNullOrWhiteSpace(c.Heading) ? null : c.Heading.Trim(),
                Subtext = string.IsNullOrWhiteSpace(c.Subtext) ? null : c.Subtext.Trim(),
                Hotspots = (c.Hotspots ?? new())
                    .Where(h => h.ProductId > 0)
                    .Select(h => new LookbookHotspot
                    {
                        X = Math.Clamp(h.X, 0, 100),
                        Y = Math.Clamp(h.Y, 0, 100),
                        ProductId = h.ProductId
                    })
                    .ToList()
            })
            .ToList();

        var json = JsonSerializer.Serialize(clean);
        await _settings.SaveManyAsync(new Dictionary<string, string> { ["lookbook.campaigns"] = json });
        await LogAsync("Update", "Setting", "lookbook.campaigns",
            $"Updated lookbook ({clean.Count} campaign(s), {clean.Sum(c => c.Hotspots.Count)} hotspot(s))");

        return Json(new { ok = true, count = clean.Count });
    }
}
