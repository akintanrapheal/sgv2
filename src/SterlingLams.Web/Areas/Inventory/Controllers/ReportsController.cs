using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

public class ReportsController : InventoryAreaController
{
    private readonly ApplicationDbContext _db;
    public ReportsController(ApplicationDbContext db) => _db = db;

    public IActionResult Index() => RedirectToAction(nameof(Reorder));

    // ── Reorder report: products at/below their low-stock threshold ──────────────
    public async Task<IActionResult> Reorder(int? categoryId = null)
    {
        ViewData["Title"] = "Reorder report";
        var stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        ViewBag.Stores = stores;
        ViewBag.Categories = await _db.Categories.OrderBy(c => c.Name).ToListAsync();
        ViewBag.CategoryId = categoryId;
        return View(await ReorderRowsAsync(categoryId, stores));
    }

    public async Task<IActionResult> ReorderCsv(int? categoryId = null)
    {
        var stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        var rows = await ReorderRowsAsync(categoryId, stores);

        var sb = new StringBuilder();
        sb.Append("Product,SKU");
        foreach (var s in stores) sb.Append(',').Append(Csv(s.Name.Replace("Sterlin Glams ", "")));
        sb.AppendLine(",Total,Threshold,Suggested reorder");
        foreach (var r in rows)
        {
            sb.Append(Csv(r.Name)).Append(',').Append(Csv(r.Sku));
            foreach (var s in stores) sb.Append(',').Append(r.PerStore[s.Id]);
            var suggest = Math.Max(0, Math.Max(1, r.Threshold) * stores.Count - r.Total);
            sb.Append(',').Append(r.Total).Append(',').Append(r.Threshold).Append(',').Append(suggest).AppendLine();
        }
        await LogAsync("Export", "Inventory", null, $"Exported reorder report ({rows.Count} product(s))");
        return CsvFile(sb, "reorder");
    }

    // Aggregation pushed to SQL: per-product totals + threshold filter run in the database, and the
    // per-store breakdown is fetched only for the (few) products at/below threshold — instead of
    // pulling the entire catalogue + every inventory row into memory.
    private async Task<List<ReorderRow>> ReorderRowsAsync(int? categoryId, List<Store> stores)
    {
        var pq = _db.Products.Where(p => p.IsActive);
        if (categoryId.HasValue) pq = pq.Where(p => p.CategoryId == categoryId.Value);

        var prods = await pq
            .Select(p => new
            {
                p.Id, p.Name, p.Sku,
                Threshold = p.LowStockThreshold,
                Total = p.StoreInventories.Sum(si => (int?)si.QuantityOnHand) ?? 0
            })
            .Where(r => r.Total <= (r.Threshold < 1 ? 1 : r.Threshold))
            .OrderBy(r => r.Total).ThenBy(r => r.Name)
            .ToListAsync();

        var ids = prods.Select(r => r.Id).ToList();
        var perStore = (await _db.StoreInventories
                .Where(si => ids.Contains(si.ProductId))
                .GroupBy(si => new { si.ProductId, si.StoreId })
                .Select(g => new { g.Key.ProductId, g.Key.StoreId, Qty = g.Sum(si => si.QuantityOnHand) })
                .ToListAsync())
            .ToLookup(x => x.ProductId);

        return prods.Select(r => new ReorderRow
        {
            Id = r.Id, Name = r.Name, Sku = r.Sku, Threshold = r.Threshold, Total = r.Total,
            PerStore = stores.ToDictionary(s => s.Id, s => perStore[r.Id].Where(x => x.StoreId == s.Id).Sum(x => x.Qty))
        }).ToList();
    }

    // ── Stock value report: units × price per branch ────────────────────────────
    public async Task<IActionResult> Value(int? categoryId = null)
    {
        ViewData["Title"] = "Stock value";
        var stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        ViewBag.Categories = await _db.Categories.OrderBy(c => c.Name).ToListAsync();
        ViewBag.CategoryId = categoryId;

        var vm = new StockValueVm
        {
            PerBranch = await PerBranchValueAsync(categoryId, stores),
            TopProducts = await ProductValuesAsync(categoryId, take: 50)
        };
        vm.TotalUnits = vm.PerBranch.Sum(b => b.Units);
        vm.TotalValue = vm.PerBranch.Sum(b => b.Value);
        return View(vm);
    }

    public async Task<IActionResult> ValueCsv(int? categoryId = null)
    {
        var rows = await ProductValuesAsync(categoryId, take: null); // full list for the export

        var sb = new StringBuilder();
        sb.AppendLine("Product,SKU,Units,Price,Value");
        foreach (var r in rows)
            sb.Append(Csv(r.Name)).Append(',').Append(Csv(r.Sku)).Append(',').Append(r.Units)
              .Append(',').Append(r.Price).Append(',').Append(r.Value).AppendLine();
        await LogAsync("Export", "Inventory", null, $"Exported stock value report ({rows.Count} product(s))");
        return CsvFile(sb, "stock_value");
    }

    // Per-branch units + value computed in SQL (join inventory→product, group by store).
    private async Task<List<BranchValue>> PerBranchValueAsync(int? categoryId, List<Store> stores)
    {
        var siq = _db.StoreInventories.Where(si => si.Product.IsActive);
        if (categoryId.HasValue) siq = siq.Where(si => si.Product.CategoryId == categoryId.Value);

        var raw = (await siq
                .GroupBy(si => si.StoreId)
                .Select(g => new
                {
                    StoreId = g.Key,
                    Units = g.Sum(si => si.QuantityOnHand),
                    Value = g.Sum(si => si.QuantityOnHand * si.Product.Price)
                })
                .ToListAsync())
            .ToDictionary(x => x.StoreId);

        return stores.Select(s => new BranchValue
        {
            Store = s.Name.Replace("Sterlin Glams ", ""),
            Units = raw.TryGetValue(s.Id, out var b) ? b.Units : 0,
            Value = raw.TryGetValue(s.Id, out var b2) ? b2.Value : 0
        }).ToList();
    }

    // Per-product units + value computed in SQL; ordered + (optionally) limited in the database.
    private async Task<List<ProductValue>> ProductValuesAsync(int? categoryId, int? take)
    {
        var pq = _db.Products.Where(p => p.IsActive);
        if (categoryId.HasValue) pq = pq.Where(p => p.CategoryId == categoryId.Value);

        var q = pq
            .Select(p => new ProductValue
            {
                Name = p.Name,
                Sku = p.Sku,
                Price = p.Price,
                Units = p.StoreInventories.Sum(si => (int?)si.QuantityOnHand) ?? 0,
                Value = (p.StoreInventories.Sum(si => (int?)si.QuantityOnHand) ?? 0) * p.Price
            })
            .Where(x => x.Units > 0)
            .OrderByDescending(x => x.Value);

        return take.HasValue ? await q.Take(take.Value).ToListAsync() : await q.ToListAsync();
    }

    private static string Csv(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
    private FileContentResult CsvFile(StringBuilder sb, string name)
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv", $"{name}_{DateTime.UtcNow:yyyyMMdd_HHmm}.csv");
    }
}

public class ReorderRow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Sku { get; set; }
    public int Threshold { get; set; }
    public int Total { get; set; }
    public Dictionary<int, int> PerStore { get; set; } = new();
}
public class StockValueVm
{
    public List<BranchValue> PerBranch { get; set; } = new();
    public List<ProductValue> TopProducts { get; set; } = new();
    public int TotalUnits { get; set; }
    public decimal TotalValue { get; set; }
}
public class BranchValue { public string Store { get; set; } = ""; public int Units { get; set; } public decimal Value { get; set; } }
public class ProductValue { public string Name { get; set; } = ""; public string? Sku { get; set; } public int Units { get; set; } public decimal Price { get; set; } public decimal Value { get; set; } }
