using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

public class StockController : InventoryAreaController
{
    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;
    private const int PageSize = 30;

    public StockController(ApplicationDbContext db, IStockService stock)
    {
        _db = db;
        _stock = stock;
    }

    public async Task<IActionResult> Index(string q = "", int page = 1)
    {
        ViewData["Title"] = "Stock";

        var stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();

        var pq = _db.Products.Include(p => p.Category).Include(p => p.Images).Where(p => p.IsActive).AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
            pq = pq.Where(p => EF.Functions.ILike(p.Name, $"%{q}%")
                            || EF.Functions.ILike(p.Sku ?? "", $"%{q}%")
                            || EF.Functions.ILike(p.Barcode ?? "", $"%{q}%"));

        var all = await pq.OrderBy(p => p.Name).ToListAsync();
        var ids = all.Select(p => p.Id).ToList();
        var inv = ids.Count > 0
            ? await _db.StoreInventories.Where(si => ids.Contains(si.ProductId)).ToListAsync()
            : new List<StoreInventory>();

        var rows = all.Select(p => new ProductInventoryRow
        {
            ProductId = p.Id,
            ProductName = p.Name,
            Sku = p.Sku,
            CategoryName = p.Category?.Name ?? "—",
            ImageUrl = p.Images.OrderBy(i => i.SortOrder).FirstOrDefault()?.Url,
            LowStockThreshold = p.LowStockThreshold,
            StockByStore = stores.ToDictionary(
                s => s.Id,
                s => inv.FirstOrDefault(si => si.ProductId == p.Id && si.StoreId == s.Id)?.QuantityOnHand ?? -1)
        }).ToList();

        var total = rows.Count;
        var pageRows = rows.Skip((page - 1) * PageSize).Take(PageSize).ToList();

        return View(new AdminInventoryViewModel
        {
            Stores = stores,
            Products = pageRows,
            SearchQuery = q,
            CategoryFilter = "",
            StockFilter = "",
            AvailableCategories = new(),
            CurrentPage = page,
            TotalPages = (int)Math.Ceiling(total / (double)PageSize),
            TotalCount = total,
        });
    }

    public class StockEdit
    {
        public int ProductId { get; set; }
        public int StoreId { get; set; }
        public int Quantity { get; set; }
    }

    // Bulk: set stock for many product×store cells, each as a traceable ledger Adjustment.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetAll([FromBody] List<StockEdit> edits)
    {
        if (edits == null || edits.Count == 0)
            return Json(new { success = true, count = 0 });

        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var validStoreIds = (await _db.Stores.Where(s => s.IsActive).Select(s => s.Id).ToListAsync()).ToHashSet();
        var validProductIds = (await _db.Products
            .Where(p => edits.Select(e => e.ProductId).Distinct().Contains(p.Id))
            .Select(p => p.Id).ToListAsync()).ToHashSet();

        var applied = 0;
        foreach (var e in edits)
        {
            if (e.Quantity < 0 || !validStoreIds.Contains(e.StoreId) || !validProductIds.Contains(e.ProductId))
                continue;

            var current = await _stock.GetStockAsync(e.ProductId, e.StoreId);
            var delta = e.Quantity - current;
            if (delta != 0)
            {
                await _stock.ApplyAsync(e.ProductId, null, e.StoreId, delta,
                    StockMovementType.Adjustment, "Inventory stock update", userId: userId);
                applied++;
            }
        }

        await _db.SaveChangesAsync();
        return Json(new { success = true, count = applied });
    }

    // Export per-branch stock levels to CSV.
    public async Task<IActionResult> ExportCsv(string q = "")
    {
        var stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        var pq = _db.Products.Where(p => p.IsActive);
        if (!string.IsNullOrWhiteSpace(q))
            pq = pq.Where(p => EF.Functions.ILike(p.Name, $"%{q}%")
                            || EF.Functions.ILike(p.Sku ?? "", $"%{q}%")
                            || EF.Functions.ILike(p.Barcode ?? "", $"%{q}%"));

        var all = await pq.OrderBy(p => p.Name).ToListAsync();
        var ids = all.Select(p => p.Id).ToList();
        var inv = ids.Count > 0
            ? await _db.StoreInventories.Where(si => ids.Contains(si.ProductId)).ToListAsync()
            : new List<StoreInventory>();

        static string Csv(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

        var sb = new StringBuilder();
        sb.Append("Product,SKU,Barcode");
        foreach (var s in stores) sb.Append(',').Append(Csv(s.Name.Replace("Sterlin Glams ", "")));
        sb.AppendLine(",Total");

        foreach (var p in all)
        {
            var byStore = stores.Select(s => inv.FirstOrDefault(si => si.ProductId == p.Id && si.StoreId == s.Id)?.QuantityOnHand ?? 0).ToList();
            sb.Append(Csv(p.Name)).Append(',').Append(Csv(p.Sku)).Append(',').Append(Csv(p.Barcode));
            foreach (var v in byStore) sb.Append(',').Append(v);
            sb.Append(',').Append(byStore.Sum()).AppendLine();
        }

        await LogAsync("Export", "Inventory", null, $"Exported stock for {all.Count} product(s) to CSV");
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv", $"stock_{DateTime.UtcNow:yyyyMMdd_HHmm}.csv");
    }
}
