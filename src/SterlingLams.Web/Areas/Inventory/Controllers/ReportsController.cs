using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;

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
        var pq = _db.Products.Where(p => p.IsActive);
        if (categoryId.HasValue) pq = pq.Where(p => p.CategoryId == categoryId.Value);
        var products = await pq.Include(p => p.StoreInventories).ToListAsync();
        var rows = products.Select(p => new ReorderRow
        {
            Id = p.Id,
            Name = p.Name,
            Sku = p.Sku,
            Threshold = p.LowStockThreshold,
            Total = p.StoreInventories.Sum(si => si.QuantityOnHand),
            PerStore = stores.ToDictionary(s => s.Id, s => p.StoreInventories.Where(si => si.StoreId == s.Id).Sum(si => si.QuantityOnHand))
        })
        .Where(r => r.Total <= Math.Max(1, r.Threshold))
        .OrderBy(r => r.Total).ThenBy(r => r.Name).ToList();
        return View(rows);
    }

    public async Task<IActionResult> ReorderCsv(int? categoryId = null)
    {
        var stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        var pq = _db.Products.Where(p => p.IsActive);
        if (categoryId.HasValue) pq = pq.Where(p => p.CategoryId == categoryId.Value);
        var products = await pq.Include(p => p.StoreInventories).ToListAsync();
        var rows = products.Select(p => new
        {
            p.Name, p.Sku, p.LowStockThreshold,
            Total = p.StoreInventories.Sum(si => si.QuantityOnHand),
            Per = stores.ToDictionary(s => s.Id, s => p.StoreInventories.Where(si => si.StoreId == s.Id).Sum(si => si.QuantityOnHand))
        }).Where(r => r.Total <= Math.Max(1, r.LowStockThreshold))
          .OrderBy(r => r.Total).ToList();

        var sb = new StringBuilder();
        sb.Append("Product,SKU");
        foreach (var s in stores) sb.Append(',').Append(Csv(s.Name.Replace("Sterlin Glams ", "")));
        sb.AppendLine(",Total,Threshold,Suggested reorder");
        foreach (var r in rows)
        {
            sb.Append(Csv(r.Name)).Append(',').Append(Csv(r.Sku));
            foreach (var s in stores) sb.Append(',').Append(r.Per[s.Id]);
            var suggest = Math.Max(0, Math.Max(1, r.LowStockThreshold) * stores.Count - r.Total);
            sb.Append(',').Append(r.Total).Append(',').Append(r.LowStockThreshold).Append(',').Append(suggest).AppendLine();
        }
        await LogAsync("Export", "Inventory", null, $"Exported reorder report ({rows.Count} product(s))");
        return CsvFile(sb, "reorder");
    }

    // ── Stock value report: units × price per branch ────────────────────────────
    public async Task<IActionResult> Value(int? categoryId = null)
    {
        ViewData["Title"] = "Stock value";
        var stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        ViewBag.Categories = await _db.Categories.OrderBy(c => c.Name).ToListAsync();
        ViewBag.CategoryId = categoryId;
        var pq = _db.Products.Where(p => p.IsActive);
        if (categoryId.HasValue) pq = pq.Where(p => p.CategoryId == categoryId.Value);
        var products = await pq.Include(p => p.StoreInventories).ToListAsync();

        var vm = new StockValueVm
        {
            PerBranch = stores.Select(s => new BranchValue
            {
                Store = s.Name.Replace("Sterlin Glams ", ""),
                Units = products.Sum(p => p.StoreInventories.Where(si => si.StoreId == s.Id).Sum(si => si.QuantityOnHand)),
                Value = products.Sum(p => p.StoreInventories.Where(si => si.StoreId == s.Id).Sum(si => si.QuantityOnHand) * p.Price)
            }).ToList(),
            TopProducts = products.Select(p => new ProductValue
            {
                Name = p.Name,
                Units = p.StoreInventories.Sum(si => si.QuantityOnHand),
                Price = p.Price,
                Value = p.StoreInventories.Sum(si => si.QuantityOnHand) * p.Price
            }).Where(x => x.Units > 0).OrderByDescending(x => x.Value).Take(50).ToList()
        };
        vm.TotalUnits = vm.PerBranch.Sum(b => b.Units);
        vm.TotalValue = vm.PerBranch.Sum(b => b.Value);
        return View(vm);
    }

    public async Task<IActionResult> ValueCsv(int? categoryId = null)
    {
        var pq = _db.Products.Where(p => p.IsActive);
        if (categoryId.HasValue) pq = pq.Where(p => p.CategoryId == categoryId.Value);
        var products = await pq.Include(p => p.StoreInventories).ToListAsync();
        var rows = products.Select(p => new
        {
            p.Name, p.Sku, p.Price,
            Units = p.StoreInventories.Sum(si => si.QuantityOnHand)
        }).Where(x => x.Units > 0).OrderByDescending(x => x.Units * x.Price).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("Product,SKU,Units,Price,Value");
        foreach (var r in rows)
            sb.Append(Csv(r.Name)).Append(',').Append(Csv(r.Sku)).Append(',').Append(r.Units)
              .Append(',').Append(r.Price).Append(',').Append(r.Units * r.Price).AppendLine();
        await LogAsync("Export", "Inventory", null, $"Exported stock value report ({rows.Count} product(s))");
        return CsvFile(sb, "stock_value");
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
public class ProductValue { public string Name { get; set; } = ""; public int Units { get; set; } public decimal Price { get; set; } public decimal Value { get; set; } }
