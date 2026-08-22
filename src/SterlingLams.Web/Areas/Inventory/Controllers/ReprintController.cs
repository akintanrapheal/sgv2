using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

/// <summary>
/// The label reprint queue: products whose price changed while they had stock on hand, so the tag
/// on the shelf is now out of date. Staff print the corrected labels (which clears the item) or
/// dismiss rows that don't need action. The queue itself is filled by the price-change hook in
/// <see cref="ApplicationDbContext"/>.
/// </summary>
public class ReprintController : InventoryAreaController
{
    private readonly ApplicationDbContext _db;
    public ReprintController(ApplicationDbContext db) => _db = db;

    public class ReprintRow
    {
        public int Id { get; set; }
        public int ProductId { get; set; }
        public string Name { get; set; } = "";
        public string? Sku { get; set; }
        public string Store { get; set; } = "";
        public int StoreId { get; set; }
        public decimal Price { get; set; }
        public decimal? SalePrice { get; set; }
        public int Stock { get; set; }
        public string Reason { get; set; } = "";
        public DateTime UpdatedAt { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Reprint labels";
        var rows = await _db.LabelReprintQueue
            .Where(q => q.Status == ReprintStatus.Pending)
            .OrderBy(q => q.Store.Name).ThenByDescending(q => q.UpdatedAt)
            .Select(q => new ReprintRow
            {
                Id = q.Id,
                ProductId = q.ProductId,
                Name = q.Product.Name,
                Sku = q.Product.Sku,
                Store = q.Store.Name,
                StoreId = q.StoreId,
                Price = q.Product.Price,
                SalePrice = q.Product.SalePrice,
                // Stock at THIS branch (the tag that needs replacing lives here).
                Stock = q.Product.StoreInventories.Where(si => si.StoreId == q.StoreId).Sum(si => (int?)si.QuantityOnHand) ?? 0,
                Reason = q.Reason,
                UpdatedAt = q.UpdatedAt
            })
            .ToListAsync();
        return View(rows);
    }

    // Mark the selected rows printed, then hand off to the existing label print sheet for those products.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Print(int[] ids)
    {
        var productIds = await ResolveAndCloseAsync(ids, ReprintStatus.Printed);
        if (productIds.Count == 0)
        {
            TempData["Error"] = "Select at least one item to print.";
            return RedirectToAction(nameof(Index));
        }
        // Reuse the standard label generator with the shop's standard tag preset: 3×1.5cm tag with
        // Name, Price, Barcode number and QR code (Labels defaults qr=false, so pass it explicitly).
        return RedirectToAction("Labels", "Products", new
        {
            area = "Inventory",
            ids = string.Join(",", productIds),
            printer = "tag30x15",
            name = true, price = true, barcodeNumber = true, qr = true,
            barcode = false, sku = false, category = false, description = false,
            font = "arial", fontSize = 9
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Dismiss(int[] ids)
    {
        var productIds = await ResolveAndCloseAsync(ids, ReprintStatus.Dismissed);
        TempData["Success"] = productIds.Count switch
        {
            0 => "Nothing was selected.",
            1 => "1 item removed from the reprint queue.",
            _ => $"{productIds.Count} items removed from the reprint queue."
        };
        return RedirectToAction(nameof(Index));
    }

    // Marks the given queue rows resolved with the outcome and returns the affected product ids.
    private async Task<List<int>> ResolveAndCloseAsync(int[] ids, ReprintStatus outcome)
    {
        if (ids == null || ids.Length == 0) return new();
        var rows = await _db.LabelReprintQueue
            .Where(q => ids.Contains(q.Id) && q.Status == ReprintStatus.Pending)
            .ToListAsync();
        if (rows.Count == 0) return new();

        var who = User?.Identity?.Name;
        var now = DateTime.UtcNow;
        foreach (var r in rows) { r.Status = outcome; r.ResolvedAt = now; r.ResolvedBy = who; }
        await _db.SaveChangesAsync();

        await LogAsync("LabelReprint", "Product", null,
            $"{(outcome == ReprintStatus.Printed ? "Printed" : "Dismissed")} {rows.Count} reprint-queue item(s).");
        return rows.Select(r => r.ProductId).Distinct().ToList();
    }
}
