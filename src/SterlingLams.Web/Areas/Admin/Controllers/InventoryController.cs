using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    public class InventoryController : AdminBaseController
    {
        protected override string Section => "Inventory";

        private readonly ApplicationDbContext _db;
        private readonly IStockService _stock;
        private const int PageSize = 30;

        public InventoryController(ApplicationDbContext db, IStockService stock)
        {
            _db = db;
            _stock = stock;
        }

        // ── Index: product-centric view (all stores as columns) ───────────────────
        public async Task<IActionResult> Index(
            string q = "", string category = "", string stock = "", int page = 1)
        {
            ViewData["Title"] = "Inventory";

            var stores = await _db.Stores
                .Where(s => s.IsActive)
                .OrderBy(s => s.Name)
                .ToListAsync();

            // ── Name + category filter in SQL ─────────────────────────────────────
            var productQuery = _db.Products
                .Include(p => p.Category)
                .Include(p => p.Images)
                .Where(p => p.IsActive)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(q))
                productQuery = productQuery.Where(p => EF.Functions.ILike(p.Name, $"%{q}%"));

            if (!string.IsNullOrWhiteSpace(category))
                productQuery = productQuery.Where(p => p.Category != null && p.Category.Slug == category);

            var allMatchingProducts = await productQuery.OrderBy(p => p.Name).ToListAsync();
            var allProductIds       = allMatchingProducts.Select(p => p.Id).ToList();

            // Load inventory for all matching products
            var allInventory = allProductIds.Any()
                ? await _db.StoreInventories.Where(si => allProductIds.Contains(si.ProductId)).ToListAsync()
                : new List<StoreInventory>();

            // Build rows (needed before stock filter so we can check stock values)
            var allRows = allMatchingProducts.Select(p =>
            {
                var stockByStore = stores.ToDictionary(
                    s => s.Id,
                    s => allInventory.FirstOrDefault(si => si.ProductId == p.Id && si.StoreId == s.Id)
                                    ?.QuantityOnHand ?? -1);
                return new ProductInventoryRow
                {
                    ProductId         = p.Id,
                    ProductName       = p.Name,
                    Sku               = p.Sku,
                    CategoryName      = p.Category?.Name ?? "—",
                    ImageUrl          = p.Images.OrderBy(i => i.SortOrder).FirstOrDefault()?.Url,
                    LowStockThreshold = p.LowStockThreshold,
                    StockByStore      = stockByStore,
                };
            }).ToList();

            // ── Stock status filter (in-memory, after building rows) ──────────────
            var filteredRows = stock switch
            {
                "outofstock" => allRows.Where(r => r.HasAnyRecord && r.TotalStock == 0).ToList(),
                "low"        => allRows.Where(r => r.HasLowStock).ToList(),
                "instock"    => allRows.Where(r => r.TotalStock > 0).ToList(),
                "norecord"   => allRows.Where(r => r.HasMissingRecord).ToList(),
                _            => allRows
            };

            // Paginate filtered rows
            var total    = filteredRows.Count;
            var pageRows = filteredRows.Skip((page - 1) * PageSize).Take(PageSize).ToList();

            var lastSync = allInventory.Any()
                ? allInventory.Max(si => (DateTime?)si.LastSyncedAt)
                : await _db.StoreInventories.MaxAsync(si => (DateTime?)si.LastSyncedAt);

            return View(new AdminInventoryViewModel
            {
                Stores              = stores,
                Products            = pageRows,
                LastSyncedAt        = lastSync,
                SearchQuery         = q,
                CategoryFilter      = category,
                StockFilter         = stock,
                AvailableCategories = await _db.Categories.OrderBy(c => c.Name).ToListAsync(),
                CurrentPage         = page,
                TotalPages          = (int)Math.Ceiling(total / (double)PageSize),
                TotalCount          = total,
            });
        }

        // ── Set stock for a product across all stores (saves absolute quantities) ─
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SetProductStock(int productId, IFormCollection form)
        {
            var product = await _db.Products.FindAsync(productId);
            if (product == null) return NotFound();

            var stores = await _db.Stores.Where(s => s.IsActive).ToListAsync();
            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

            foreach (var store in stores)
            {
                var key = $"store_{store.Id}";
                if (!form.TryGetValue(key, out var qtyStr)) continue;
                if (!int.TryParse(qtyStr, out var qty) || qty < 0) continue;

                // Apply the change through the stock ledger so every restock is traceable.
                var current = await _stock.GetStockAsync(productId, store.Id);
                var delta = qty - current;
                if (delta != 0)
                    await _stock.ApplyAsync(productId, null, store.Id, delta,
                        StockMovementType.Adjustment, "Stock update", userId: userId);
            }

            await _db.SaveChangesAsync();

            var summary = string.Join(", ", stores.Select(s =>
                $"{s.Name.Replace("Sterlin Glams ", "")}: {form[$"store_{s.Id}"]}"));
            await LogAsync("Update", "Inventory", productId.ToString(),
                $"Set stock for '{product.Name}' — {summary}");

            TempData["Success"] = $"Stock updated for '{product.Name}'.";

            var q        = form["q"].ToString();
            var category = form["category"].ToString();
            var stock    = form["stock"].ToString();
            var page     = int.TryParse(form["page"], out var p) ? p : 1;
            return RedirectToAction(nameof(Index), new { q, category, stock, page });
        }

        // ── Ensure all product × store combinations have an inventory record ──────
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> EnsureInventoryRecords()
        {
            var products = await _db.Products.Where(p => p.IsActive).Select(p => p.Id).ToListAsync();
            var stores   = await _db.Stores.Where(s => s.IsActive).Select(s => s.Id).ToListAsync();

            var existing = await _db.StoreInventories
                .Select(si => new { si.ProductId, si.StoreId })
                .ToListAsync();

            var existingSet = existing.Select(e => (e.ProductId, e.StoreId)).ToHashSet();
            int created = 0;

            foreach (var productId in products)
            {
                foreach (var storeId in stores)
                {
                    if (!existingSet.Contains((productId, storeId)))
                    {
                        _db.StoreInventories.Add(new StoreInventory
                        {
                            ProductId      = productId,
                            StoreId        = storeId,
                            QuantityOnHand = 0,
                            LastSyncedAt   = DateTime.UtcNow,
                        });
                        created++;
                    }
                }
            }

            await _db.SaveChangesAsync();
            if (created > 0)
                await LogAsync("Update", "Inventory", null, $"Created {created} missing inventory record(s)");
            TempData["Success"] = $"Created {created} missing inventory record(s). All products now appear in the grid.";
            return RedirectToAction(nameof(Index));
        }

        // ── Update low-stock threshold for a product ──────────────────────────────
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateThreshold(int productId, int threshold)
        {
            var product = await _db.Products.FindAsync(productId);
            if (product == null) return NotFound();

            product.LowStockThreshold = Math.Max(0, threshold);
            product.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            await LogAsync("Update", "Inventory", productId.ToString(),
                $"Set low-stock threshold for '{product.Name}' to {threshold}");
            TempData["Success"] = $"Threshold for '{product.Name}' set to {threshold}.";
            return RedirectToAction(nameof(Index));
        }
    }
}
