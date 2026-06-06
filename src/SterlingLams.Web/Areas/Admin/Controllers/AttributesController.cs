using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Admin.Controllers;

public class AttributesController : AdminBaseController
{
    private readonly ApplicationDbContext _db;

    public AttributesController(ApplicationDbContext db) => _db = db;

    // ── Index ─────────────────────────────────────────────────────────────────
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Attributes";
        var attrs = await _db.ProductAttributes
            .Include(a => a.Values.OrderBy(v => v.SortOrder))
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Name)
            .ToListAsync();
        return View(new AdminAttributeListViewModel { Attributes = attrs });
    }

    // ── Create ────────────────────────────────────────────────────────────────
    public IActionResult Create()
    {
        ViewData["Title"] = "New Attribute";
        return View("Edit", new AdminAttributeEditViewModel { IsActive = true });
    }

    // ── Edit ──────────────────────────────────────────────────────────────────
    public async Task<IActionResult> Edit(int id)
    {
        ViewData["Title"] = "Edit Attribute";
        var attr = await _db.ProductAttributes
            .Include(a => a.Values.OrderBy(v => v.SortOrder))
            .FirstOrDefaultAsync(a => a.Id == id);
        if (attr == null) return NotFound();

        return View(new AdminAttributeEditViewModel
        {
            Id        = attr.Id,
            Name      = attr.Name,
            Slug      = attr.Slug,
            IsActive  = attr.IsActive,
            SortOrder = attr.SortOrder,
            Values    = attr.Values.ToList(),
        });
    }

    // ── Save (create or update attribute) ────────────────────────────────────
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(AdminAttributeEditViewModel vm)
    {
        if (!ModelState.IsValid)
        {
            vm.Values = vm.Id == 0 ? new() :
                await _db.ProductAttributeValues
                    .Where(v => v.AttributeId == vm.Id)
                    .OrderBy(v => v.SortOrder).ToListAsync();
            ViewData["Title"] = vm.Id == 0 ? "New Attribute" : "Edit Attribute";
            return View("Edit", vm);
        }

        var slug = string.IsNullOrWhiteSpace(vm.Slug)
            ? Slugify(vm.Name)
            : vm.Slug.Trim().ToLowerInvariant();

        ProductAttribute attr;
        if (vm.Id == 0)
        {
            attr = new ProductAttribute();
            _db.ProductAttributes.Add(attr);
        }
        else
        {
            attr = await _db.ProductAttributes.FindAsync(vm.Id) ?? new ProductAttribute();
        }

        attr.Name      = vm.Name.Trim();
        attr.Slug      = slug;
        attr.IsActive  = vm.IsActive;
        attr.SortOrder = vm.SortOrder;

        await _db.SaveChangesAsync();
        TempData["Success"] = $"Attribute '{attr.Name}' saved.";
        return RedirectToAction(nameof(Edit), new { id = attr.Id });
    }

    // ── Add Value ─────────────────────────────────────────────────────────────
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddValue(int id, string value, string? colorHex)
    {
        var attr = await _db.ProductAttributes.FindAsync(id);
        if (attr == null) return NotFound();

        if (!string.IsNullOrWhiteSpace(value))
        {
            var maxSort = await _db.ProductAttributeValues
                .Where(v => v.AttributeId == id)
                .MaxAsync(v => (int?)v.SortOrder) ?? 0;

            _db.ProductAttributeValues.Add(new ProductAttributeValue
            {
                AttributeId = id,
                Value       = value.Trim(),
                ColorHex    = string.IsNullOrWhiteSpace(colorHex) ? null : colorHex.Trim(),
                SortOrder   = maxSort + 1,
            });
            await _db.SaveChangesAsync();
            TempData["Success"] = $"Value '{value}' added.";
        }

        return RedirectToAction(nameof(Edit), new { id });
    }

    // ── Delete Value ──────────────────────────────────────────────────────────
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteValue(int attributeId, int valueId)
    {
        var value = await _db.ProductAttributeValues.FindAsync(valueId);
        if (value != null && value.AttributeId == attributeId)
        {
            _db.ProductAttributeValues.Remove(value);
            await _db.SaveChangesAsync();
            TempData["Success"] = "Value removed.";
        }
        return RedirectToAction(nameof(Edit), new { id = attributeId });
    }

    // ── Delete Attribute ──────────────────────────────────────────────────────
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var attr = await _db.ProductAttributes.FindAsync(id);
        if (attr != null)
        {
            _db.ProductAttributes.Remove(attr);
            await _db.SaveChangesAsync();
            TempData["Success"] = $"Attribute '{attr.Name}' deleted.";
        }
        return RedirectToAction(nameof(Index));
    }

    private static string Slugify(string name) =>
        System.Text.RegularExpressions.Regex
            .Replace(name.ToLowerInvariant().Trim(), @"[^a-z0-9]+", "-")
            .Trim('-');
}
