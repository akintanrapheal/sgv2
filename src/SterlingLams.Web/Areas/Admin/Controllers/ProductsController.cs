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

            var product = await _db.Products
                .Include(p => p.Images)
                .FirstOrDefaultAsync(p => p.Id == id);
            if (product == null) return NotFound();

            var vm = new AdminProductEditViewModel
            {
                Id = product.Id,
                Name = product.Name,
                Slug = product.Slug,
                Description = product.Description ?? "",
                ShortDescription = product.ShortDescription,
                Price = product.Price,
                Colour = product.Metal,
                Weight = product.Weight,
                IsActive = product.IsActive,
                IsFeatured = product.IsFeatured,
                IsNewArrival = product.IsNewArrival,
                ErpNextItemCode = product.ErpNextItemCode,
                CategoryId = product.CategoryId,
                Categories = await _db.Categories.OrderBy(c => c.Name).ToListAsync(),
                Images = product.Images.OrderBy(i => i.SortOrder).ToList()
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
            product.ShortDescription = vm.ShortDescription;
            product.Price = vm.Price;
            product.Metal = vm.Colour;
            product.Weight = vm.Weight;
            product.IsActive = vm.IsActive;
            product.IsFeatured = vm.IsFeatured;
            product.IsNewArrival = vm.IsNewArrival;
            product.ErpNextItemCode = vm.ErpNextItemCode?.Trim() ?? string.Empty;
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
        public async Task<IActionResult> SyncFromERPNext()
        {
            try
            {
                await _inventory.SyncAllAsync();
                TempData["Success"] = "ERPNext inventory sync completed successfully.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Inventory sync failed: {ex.Message}";
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ImportFromERPNext()
        {
            try
            {
                var result = await _importer.ImportAllFromERPNextAsync();
                TempData[result.Success ? "Success" : "Warning"] =
                    $"ERPNext product import complete: {result.Summary}" +
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

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddImage(int id, string imageUrl, string? altText, bool isPrimary)
        {
            var product = await _db.Products.FindAsync(id);
            if (product == null) return NotFound();

            if (isPrimary)
            {
                var existing = await _db.ProductImages
                    .Where(i => i.ProductId == id && i.IsPrimary)
                    .ToListAsync();
                existing.ForEach(i => i.IsPrimary = false);
            }

            var maxSort = await _db.ProductImages
                .Where(i => i.ProductId == id)
                .MaxAsync(i => (int?)i.SortOrder) ?? 0;

            _db.ProductImages.Add(new ProductImage
            {
                ProductId = id,
                Url = imageUrl.Trim(),
                AltText = altText?.Trim(),
                IsPrimary = isPrimary,
                SortOrder = maxSort + 1
            });

            await _db.SaveChangesAsync();
            TempData["Success"] = "Image added.";
            return RedirectToAction(nameof(Edit), new { id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteImage(int productId, int imageId)
        {
            var image = await _db.ProductImages.FindAsync(imageId);
            if (image != null && image.ProductId == productId)
            {
                _db.ProductImages.Remove(image);
                await _db.SaveChangesAsync();
                TempData["Success"] = "Image removed.";
            }
            return RedirectToAction(nameof(Edit), new { id = productId });
        }
    }
}
