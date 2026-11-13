using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.Areas.Admin.Controllers;

/// <summary>Curate the storefront Sale page (/sale): pick which products appear, and launch it to the
/// public when ready. "On the Sale page" is <see cref="SterlingLams.Web.Models.Domain.Product.ShowOnSalePage"/>.</summary>
public class SaleController : AdminBaseController
{
    protected override string? Section => "Sale";

    private readonly ApplicationDbContext _db;
    private readonly SterlingLams.Web.Services.ISettingsService _settings;
    public SaleController(ApplicationDbContext db, SterlingLams.Web.Services.ISettingsService settings)
    {
        _db = db;
        _settings = settings;
    }

    public async Task<IActionResult> Index(string? q, bool selectedOnly = false)
    {
        ViewData["Title"] = "Sale page";

        var query = _db.Products.AsNoTracking().Where(p => p.IsActive);
        if (selectedOnly) query = query.Where(p => p.ShowOnSalePage);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim();
            query = query.Where(p => EF.Functions.ILike(p.Name, $"%{t}%")
                || EF.Functions.ILike(p.Sku ?? "", $"%{t}%"));
        }

        var items = await query
            .OrderByDescending(p => p.ShowOnSalePage).ThenBy(p => p.Name)
            .Take(300)
            .Select(p => new SaleAdminItem(
                p.Id, p.Name, p.Sku, p.Price, p.SalePrice, p.ShowOnSalePage,
                p.Images.OrderByDescending(i => i.IsPrimary).ThenBy(i => i.SortOrder).Select(i => i.Url).FirstOrDefault()))
            .ToListAsync();

        ViewBag.Query = q;
        ViewBag.SelectedOnly = selectedOnly;
        ViewBag.SelectedCount = await _db.Products.CountAsync(p => p.ShowOnSalePage && p.IsActive);
        ViewBag.SalePublic = await _settings.GetBoolAsync("sale.public", false);
        return View(items);
    }

    /// <summary>Add/remove one product to/from the Sale page (AJAX). Returns the new selected count.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle(int id, bool on)
    {
        var p = await _db.Products.FindAsync(id);
        if (p == null) return NotFound();
        p.ShowOnSalePage = on;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await LogAsync("Update", "Product", id.ToString(),
            $"{(on ? "Added to" : "Removed from")} the Sale page: '{p.Name}'");
        return Json(new { ok = true, on, count = await _db.Products.CountAsync(x => x.ShowOnSalePage && x.IsActive) });
    }

    /// <summary>Launch the Sale page to customers, or take it back to staff-only.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPublic(bool on)
    {
        await _settings.SaveManyAsync(new Dictionary<string, string> { ["sale.public"] = on ? "true" : "false" });
        await LogAsync("Update", "Setting", "sale.public",
            on ? "Sale page made public (live for customers)" : "Sale page hidden (staff only)");
        TempData["Success"] = on
            ? "The Sale page is now live for customers."
            : "The Sale page is now visible to staff only.";
        return RedirectToAction(nameof(Index));
    }
}

public record SaleAdminItem(int Id, string Name, string? Sku, decimal Price, decimal? SalePrice, bool OnSale, string? Image);
