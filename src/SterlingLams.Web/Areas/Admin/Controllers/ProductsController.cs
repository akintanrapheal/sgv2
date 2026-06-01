using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using SterlingLams.Web.Services.Inventory;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    public class ProductsController : AdminBaseController
    {
        private readonly ApplicationDbContext _db;
        private readonly IInventoryService _inventory;
        private readonly IProductImportService _importer;
        private const int PageSize = 30;

        public ProductsController(
            ApplicationDbContext db,
            IInventoryService inventory,
            IProductImportService importer)
        {
            _db = db;
            _inventory = inventory;
            _importer = importer;
        }

        public async Task<IActionResult> Index(string q = "", int page = 1)
        {
            ViewData["Title"] = "Products";

            var query = _db.Products
                .Include(p => p.Category)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(p => EF.Functions.ILike(p.Name, $"%{q}%"));

            var total = await query.CountAsync();
            var products = await query
                .OrderBy(p => p.Name)
                .Skip((page - 1) * PageSize)
                .Take(PageSize)
                .ToListAsync();

            var vm = new AdminProductListViewModel
            {
                Products = products,
                SearchQuery = q,
                CurrentPage = page,
                TotalPages = (int)Math.Ceiling(total / (double)PageSize)
            };

            return View(vm);
        }

        public async Task<IActionResult> Create()
        {
            ViewData["Title"] = "New Product";
            var vm = new AdminProductEditViewModel
            {
                Categories = await _db.Categories.OrderBy(c => c.Name).ToListAsync()
            };
            return View("Edit", vm);
        }

        public async Task<IActionResult> Edit(int id)
        {
            ViewData["Title"] = "Edit Product";

            var product = await _db.Products.FindAsync(id);
            if (product == null) return NotFound();

            var vm = new AdminProductEditViewModel
            {
                Id = product.Id,
                Name = product.Name,
                Slug = product.Slug,
                Description = product.Description ?? "",
                Price = product.Price,
                Material = product.Material,
                Carat = product.Carat,
                GemstoneType = product.GemstoneType,
                IsActive = product.IsActive,
                IsFeatured = product.IsFeatured,
                OdooProductId = product.OdooProductId,
                CategoryId = product.CategoryId,
                Categories = await _db.Categories.OrderBy(c => c.Name).ToListAsync()
            };

            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(AdminProductEditViewModel vm)
        {
            vm.Categories = await _db.Categories.OrderBy(c => c.Name).ToListAsync();

            if (!ModelState.IsValid)
            {
                ViewData["Title"] = vm.Id == 0 ? "New Product" : "Edit Product";
                return View("Edit", vm);
            }

            Product product;
            if (vm.Id == 0)
            {
                product = new Product();
                _db.Products.Add(product);
            }
            else
            {
                product = await _db.Products.FindAsync(vm.Id) ?? new Product();
            }

            product.Name = vm.Name.Trim();
            product.Slug = string.IsNullOrWhiteSpace(vm.Slug)
                ? Regex.Replace(vm.Name.ToLower().Trim(), @"[^a-z0-9]+", "-")
                : vm.Slug.Trim();
            product.Description = vm.Description;
            product.Price = vm.Price;
            product.Material = vm.Material;
            product.Carat = vm.Carat;
            product.GemstoneType = vm.GemstoneType;
            product.IsActive = vm.IsActive;
            product.IsFeatured = vm.IsFeatured;
            product.OdooProductId = vm.OdooProductId;
            product.CategoryId = vm.CategoryId ?? product.CategoryId;
            product.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            TempData["Success"] = $"Product '{product.Name}' saved.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleActive(int id)
        {
            var product = await _db.Products.FindAsync(id);
            if (product == null) return NotFound();

            product.IsActive = !product.IsActive;
            product.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            TempData["Success"] = $"'{product.Name}' is now {(product.IsActive ? "active" : "inactive")}.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SyncFromOdoo()
        {
            try
            {
                await _inventory.SyncAllAsync();
                TempData["Success"] = "Odoo inventory sync completed successfully.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Inventory sync failed: {ex.Message}";
            }

            return RedirectToAction(nameof(Index));
        }

        /// <summary>
        /// Import all products from Odoo into local database (upsert by OdooProductId).
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ImportFromOdoo()
        {
            try
            {
                var result = await _importer.ImportAllFromOdooAsync();
                TempData[result.Success ? "Success" : "Warning"] =
                    $"Odoo product import complete: {result.Summary}" +
                    (result.Errors.Any() ? $" — First error: {result.Errors[0]}" : "");
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Product import failed: {ex.Message}";
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var product = await _db.Products.FindAsync(id);
            if (product != null)
            {
                _db.Products.Remove(product);
                await _db.SaveChangesAsync();
                TempData["Success"] = $"Product '{product.Name}' deleted.";
            }

            return RedirectToAction(nameof(Index));
        }
    }
}
