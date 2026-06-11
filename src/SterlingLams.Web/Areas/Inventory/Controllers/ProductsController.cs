using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

public class ProductsController : InventoryAreaController
{
    private readonly ApplicationDbContext _db;
    private const int PageSize = 30;
    public ProductsController(ApplicationDbContext db) => _db = db;

    // List — search matches name, SKU OR barcode (so a scanner finds the product).
    public async Task<IActionResult> Index(string q = "", int page = 1)
    {
        ViewData["Title"] = "Products";
        var query = _db.Products.Include(p => p.Category).Include(p => p.Images).Where(p => p.IsActive == true || p.IsActive == false);

        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(p => EF.Functions.ILike(p.Name, $"%{q}%")
                                  || EF.Functions.ILike(p.Sku ?? "", $"%{q}%")
                                  || EF.Functions.ILike(p.Barcode ?? "", $"%{q}%")
                                  || p.Variants.Any(v => EF.Functions.ILike(v.Barcode ?? "", $"%{q}%")));

        var total = await query.CountAsync();
        var products = await query.OrderBy(p => p.Name)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .Select(p => new InvProductRow
            {
                Id = p.Id,
                Name = p.Name,
                Sku = p.Sku,
                Barcode = p.Barcode,
                Price = p.Price,
                CategoryName = p.Category != null ? p.Category.Name : "—",
                ImageUrl = p.Images.OrderBy(i => i.SortOrder).Select(i => i.Url).FirstOrDefault(),
                IsActive = p.IsActive,
                TotalStock = p.StoreInventories.Sum(si => (int?)si.QuantityOnHand) ?? 0
            })
            .ToListAsync();

        ViewBag.Query = q;
        ViewBag.Page = page;
        ViewBag.TotalPages = (int)Math.Ceiling(total / (double)PageSize);
        ViewBag.Total = total;
        return View(products);
    }

    public async Task<IActionResult> Edit(int id)
    {
        var product = await _db.Products
            .Include(p => p.Variants.OrderBy(v => v.Name))
            .FirstOrDefaultAsync(p => p.Id == id);
        if (product == null) return NotFound();
        ViewData["Title"] = "Edit Product";
        await LoadCategories(product.CategoryId);
        return View(product);
    }

    // Save per-variant barcodes (parallel arrays from the variants table).
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveVariants(int productId, int[] variantId, string[] barcode)
    {
        var variants = await _db.ProductVariants.Where(v => v.ProductId == productId).ToListAsync();
        for (int i = 0; variantId != null && i < variantId.Length; i++)
        {
            var v = variants.FirstOrDefault(x => x.Id == variantId[i]);
            if (v != null)
                v.Barcode = (barcode != null && i < barcode.Length && !string.IsNullOrWhiteSpace(barcode[i]))
                    ? barcode[i].Trim() : null;
        }
        await _db.SaveChangesAsync();
        await LogAsync("Update", "Product", productId.ToString(), "Updated variant barcodes");
        TempData["Success"] = "Variant barcodes saved.";
        return RedirectToAction(nameof(Edit), new { id = productId });
    }

    public async Task<IActionResult> Create()
    {
        ViewData["Title"] = "New Product";
        await LoadCategories(null);
        return View("Edit", new Product { IsActive = true, Currency = "NGN", LowStockThreshold = 3 });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(int id, string name, string? sku, string? barcode, decimal price,
        int? categoryId, int lowStockThreshold, bool isActive, string? description)
    {
        if (string.IsNullOrWhiteSpace(name) || categoryId == null)
        {
            TempData["Error"] = string.IsNullOrWhiteSpace(name) ? "Name is required." : "Please choose a category.";
            return RedirectToAction(id == 0 ? nameof(Create) : nameof(Edit), id == 0 ? null : new { id });
        }

        var isNew = id == 0;
        var product = isNew ? new Product() : await _db.Products.FindAsync(id);
        if (product == null) return NotFound();

        product.Name = name.Trim();
        product.Sku = string.IsNullOrWhiteSpace(sku) ? null : sku.Trim();
        product.Barcode = string.IsNullOrWhiteSpace(barcode) ? null : barcode.Trim();
        product.Price = price;
        product.CategoryId = categoryId.Value;
        product.LowStockThreshold = lowStockThreshold;
        product.IsActive = isActive;
        product.Description = description;
        product.UpdatedAt = DateTime.UtcNow;

        if (isNew)
        {
            product.Currency = "NGN";
            product.ProductType = "simple";
            product.ExternalCode = "";
            product.CreatedAt = DateTime.UtcNow;
            product.Slug = await UniqueSlugAsync(Slugify(name));
            _db.Products.Add(product);
        }

        await _db.SaveChangesAsync();
        await EnsureInventoryRecordsAsync(product.Id);
        await LogAsync(isNew ? "Create" : "Update", "Product", product.Id.ToString(),
            $"{(isNew ? "Created" : "Updated")} product '{product.Name}'" + (string.IsNullOrEmpty(product.Barcode) ? "" : $" (barcode {product.Barcode})"));

        TempData["Success"] = $"'{product.Name}' saved.";
        return RedirectToAction(nameof(Index));
    }

    // Printable barcode label sheet for the selected products.
    public async Task<IActionResult> Labels(string ids)
    {
        var idList = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0).Distinct().ToList();
        var products = await _db.Products.Where(p => idList.Contains(p.Id))
            .Select(p => new LabelRow { Name = p.Name, Price = p.Price, Code = p.Barcode ?? p.Sku ?? ("P" + p.Id) })
            .ToListAsync();
        ViewData["Title"] = "Barcode Labels";
        return View(products);
    }

    // Full stock-movement ledger for one product (every sale / adjustment / transfer).
    public async Task<IActionResult> History(int id)
    {
        var product = await _db.Products.FindAsync(id);
        if (product == null) return NotFound();
        ViewData["Title"] = "Stock history";
        ViewBag.ProductName = product.Name;
        ViewBag.ProductId = id;
        var moves = await _db.StockMovements.Where(m => m.ProductId == id)
            .Include(m => m.Store)
            .OrderByDescending(m => m.Id).Take(300)
            .Select(m => new MovementHistoryRow
            {
                Date = m.CreatedAt,
                Store = m.Store.Name.Replace("Sterlin Glams ", ""),
                Type = m.Type.ToString(),
                Change = m.QuantityChange,
                Balance = m.BalanceAfter,
                Reference = m.Reference,
                Note = m.Note
            })
            .ToListAsync();
        return View(moves);
    }

    // Look up a product by exact barcode (for scan boxes). Returns id/name or 404.
    [HttpGet]
    public async Task<IActionResult> Lookup(string barcode)
    {
        barcode = (barcode ?? "").Trim();
        if (barcode.Length == 0) return Json(new { found = false });
        var p = await _db.Products
            .Where(x => x.Barcode == barcode || x.Sku == barcode
                     || x.Variants.Any(v => v.Barcode == barcode || v.Sku == barcode))
            .Select(x => new { x.Id, x.Name, x.Sku, x.Barcode })
            .FirstOrDefaultAsync();
        return p == null ? Json(new { found = false }) : Json(new { found = true, id = p.Id, name = p.Name });
    }

    private async Task LoadCategories(int? selected)
    {
        ViewBag.Categories = await _db.Categories.OrderBy(c => c.Name)
            .Select(c => new SelectListItem { Value = c.Id.ToString(), Text = c.Name, Selected = c.Id == selected })
            .ToListAsync();
    }

    private async Task EnsureInventoryRecordsAsync(int productId)
    {
        var storeIds = await _db.Stores.Where(s => s.IsActive).Select(s => s.Id).ToListAsync();
        var existing = await _db.StoreInventories.Where(si => si.ProductId == productId).Select(si => si.StoreId).ToListAsync();
        foreach (var sid in storeIds.Except(existing))
            _db.StoreInventories.Add(new StoreInventory { ProductId = productId, StoreId = sid, QuantityOnHand = 0 });
        await _db.SaveChangesAsync();
    }

    private static string Slugify(string s)
    {
        s = (s ?? "").ToLowerInvariant().Trim();
        s = Regex.Replace(s, @"[^a-z0-9\s-]", "");
        s = Regex.Replace(s, @"[\s-]+", "-").Trim('-');
        return string.IsNullOrEmpty(s) ? "product" : s;
    }
    private async Task<string> UniqueSlugAsync(string baseSlug)
    {
        var slug = baseSlug; var n = 1;
        while (await _db.Products.AnyAsync(p => p.Slug == slug)) slug = $"{baseSlug}-{++n}";
        return slug;
    }
}

public class LabelRow
{
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public string Code { get; set; } = "";
}

public class MovementHistoryRow
{
    public DateTime Date { get; set; }
    public string Store { get; set; } = "";
    public string Type { get; set; } = "";
    public int Change { get; set; }
    public int Balance { get; set; }
    public string? Reference { get; set; }
    public string? Note { get; set; }
}

public class InvProductRow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Sku { get; set; }
    public string? Barcode { get; set; }
    public decimal Price { get; set; }
    public string CategoryName { get; set; } = "";
    public string? ImageUrl { get; set; }
    public bool IsActive { get; set; }
    public int TotalStock { get; set; }
}
