using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

public class StocktakeController : InventoryAreaController
{
    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;
    private const int PageSize = 40;

    public StocktakeController(ApplicationDbContext db, IStockService stock)
    {
        _db = db;
        _stock = stock;
    }

    // Count sheet for one branch: enter physical counts, see variance, reconcile.
    public async Task<IActionResult> Index(int? storeId, string q = "", int page = 1)
    {
        ViewData["Title"] = "Stock-take";
        ViewBag.Stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        ViewBag.StoreId = storeId;
        ViewBag.Q = q;
        ViewBag.Page = page;

        if (storeId == null) return View(new List<StocktakeRow>());

        var pq = _db.Products.Where(p => p.IsActive);
        if (!string.IsNullOrWhiteSpace(q))
            pq = pq.Where(p => EF.Functions.ILike(p.Name, $"%{q}%")
                            || EF.Functions.ILike(p.Sku ?? "", $"%{q}%")
                            || EF.Functions.ILike(p.Barcode ?? "", $"%{q}%")
                            || p.Variants.Any(v => EF.Functions.ILike(v.Barcode ?? "", $"%{q}%")));

        var rows = await pq.OrderBy(p => p.Name)
            .Select(p => new StocktakeRow
            {
                ProductId = p.Id,
                Name = p.Name,
                Sku = p.Sku,
                System = p.StoreInventories.Where(si => si.StoreId == storeId).Select(si => (int?)si.QuantityOnHand).FirstOrDefault() ?? 0
            })
            .ToListAsync();

        ViewBag.TotalPages = (int)Math.Ceiling(rows.Count / (double)PageSize);
        return View(rows.Skip((page - 1) * PageSize).Take(PageSize).ToList());
    }

    public class CountEntry { public int ProductId { get; set; } public int Counted { get; set; } }
    public class StocktakeRequest { public int StoreId { get; set; } public List<CountEntry> Counts { get; set; } = new(); }

    // Reconcile: set the system quantity to the counted value (ledger Adjustment, reason "Stock-take").
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Apply([FromBody] StocktakeRequest req)
    {
        var store = await _db.Stores.FirstOrDefaultAsync(s => s.Id == req.StoreId && s.IsActive);
        if (store == null) return Json(new { success = false, message = "Invalid branch." });
        if (req.Counts == null || req.Counts.Count == 0) return Json(new { success = true, count = 0 });

        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var ids = req.Counts.Select(c => c.ProductId).Distinct().ToList();
        var validIds = (await _db.Products.Where(p => ids.Contains(p.Id)).Select(p => p.Id).ToListAsync()).ToHashSet();

        var applied = 0;
        foreach (var c in req.Counts)
        {
            if (c.Counted < 0 || !validIds.Contains(c.ProductId)) continue;
            var current = await _stock.GetStockAsync(c.ProductId, store.Id);
            var delta = c.Counted - current;
            if (delta != 0)
            {
                await _stock.ApplyAsync(c.ProductId, null, store.Id, delta,
                    StockMovementType.Adjustment, "Stock-take", userId: userId);
                applied++;
            }
        }

        await _db.SaveChangesAsync();
        await LogAsync("Update", "Inventory", null, $"Stock-take at {store.Name} — {applied} adjustment(s)");
        return Json(new { success = true, count = applied });
    }
}

public class StocktakeRow
{
    public int ProductId { get; set; }
    public string Name { get; set; } = "";
    public string? Sku { get; set; }
    public int System { get; set; }
}
