using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers;

/// <summary>Admin tool to generate SEO-friendly product descriptions per category (preview → apply).
/// Runs against whatever database the app is connected to, so it updates production directly when
/// used on the live site — no DB credentials to share.</summary>
public class SeoController : AdminBaseController
{
    private readonly ApplicationDbContext _db;
    private readonly SeoDescriptionGenerator _gen;

    // Full administrators only (Section == null).
    public SeoController(ApplicationDbContext db, SeoDescriptionGenerator gen)
    {
        _db = db;
        _gen = gen;
    }

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "SEO Descriptions";
        ViewBag.Categories = await _db.Categories.OrderBy(c => c.Name)
            .Select(c => new CatRow { Id = c.Id, Name = c.Name, Count = _db.Products.Count(p => p.CategoryId == c.Id) })
            .Where(c => c.Count > 0)
            .ToListAsync();
        return View();
    }

    // Sample generated descriptions for a category (no DB writes) — for the review step.
    [HttpGet]
    public async Task<IActionResult> Preview(int categoryId)
    {
        var cat = await _db.Categories.FirstOrDefaultAsync(c => c.Id == categoryId);
        if (cat == null) return Json(new { ok = false, error = "Category not found." });

        var total = await _db.Products.CountAsync(p => p.CategoryId == categoryId);
        // A spread of names so the samples show different styles (stud/hoop/drop, pearl/stone…).
        var prods = await _db.Products.Where(p => p.CategoryId == categoryId)
            .OrderBy(p => p.Name).Select(p => new { p.Id, p.Name }).ToListAsync();
        var step = Math.Max(1, prods.Count / 6);
        var picks = prods.Where((_, i) => i % step == 0).Take(6).ToList();

        var samples = picks.Select(p => new
        {
            name = p.Name,
            shortText = _gen.BuildShort(p.Id, p.Name, cat.Name),
            html = _gen.Build(p.Id, p.Name, cat.Name)
        }).ToList();
        return Json(new { ok = true, total, category = cat.Name, samples });
    }

    // Generate + save descriptions for every product in the category.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Apply(int categoryId, bool overwrite = true)
    {
        var cat = await _db.Categories.FirstOrDefaultAsync(c => c.Id == categoryId);
        if (cat == null) return Json(new { ok = false, error = "Category not found." });

        var q = _db.Products.Where(p => p.CategoryId == categoryId);
        if (!overwrite) q = q.Where(p => p.Description == null || p.Description == "");
        var prods = await q.ToListAsync();

        var now = DateTime.UtcNow;
        foreach (var p in prods)
        {
            p.Description = ProductHtml.Sanitize(_gen.Build(p.Id, p.Name, cat.Name));
            p.ShortDescription = _gen.BuildShort(p.Id, p.Name, cat.Name);
            p.UpdatedAt = now;
        }
        await _db.SaveChangesAsync();
        await LogAsync("Update", "Product", null, $"Generated SEO descriptions for {prods.Count} product(s) in '{cat.Name}'");
        return Json(new { ok = true, count = prods.Count, category = cat.Name });
    }

    public class CatRow { public int Id { get; set; } public string Name { get; set; } = ""; public int Count { get; set; } }
}
