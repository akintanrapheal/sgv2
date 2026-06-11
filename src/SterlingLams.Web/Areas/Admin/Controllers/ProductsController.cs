using System;
using System.Linq;
using System.Text.RegularExpressions;
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
    public class ProductsController : AdminBaseController
    {
        protected override string Section => "Products";

        private readonly ApplicationDbContext _db;
        private readonly IWooCommerceImportService _wooImporter;
        private readonly IWebHostEnvironment _env;
        private const int PageSize = 30;

        public ProductsController(
            ApplicationDbContext db,
            IWooCommerceImportService wooImporter,
            IWebHostEnvironment env)
        {
            _db = db;
            _wooImporter = wooImporter;
            _env = env;
        }

        public async Task<IActionResult> Index(
            string q = "", string category = "", string status = "", string type = "",
            decimal? minPrice = null, decimal? maxPrice = null, int page = 1)
        {
            ViewData["Title"] = "Products";

            var query = _db.Products
                .Include(p => p.Category)
                .Include(p => p.Images)          // ← images for thumbnails
                .Include(p => p.Variants)        // ← for the variant-count badge
                .AsQueryable();

            // ── Filters ──────────────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(p => EF.Functions.ILike(p.Name, $"%{q}%"));

            if (!string.IsNullOrWhiteSpace(category))
                query = query.Where(p => p.Category != null && p.Category.Slug == category);

            if (minPrice.HasValue)
                query = query.Where(p => p.Price >= minPrice.Value);

            if (maxPrice.HasValue)
                query = query.Where(p => p.Price <= maxPrice.Value);

            if (type == "variable")
                query = query.Where(p => p.ProductType == "variable");
            else if (type == "simple")
                query = query.Where(p => p.ProductType != "variable");

            switch (status)
            {
                case "active":   query = query.Where(p => p.IsActive);           break;
                case "inactive": query = query.Where(p => !p.IsActive);          break;
                case "featured": query = query.Where(p => p.IsFeatured);         break;
                case "new":      query = query.Where(p => p.IsNewArrival);       break;
            }

            var total    = await query.CountAsync();
            var products = await query
                .OrderBy(p => p.Name)
                .Skip((page - 1) * PageSize)
                .Take(PageSize)
                .ToListAsync();

            var vm = new AdminProductListViewModel
            {
                Products            = products,
                SearchQuery         = q,
                CategoryFilter      = category,
                StatusFilter        = status,
                TypeFilter          = type,
                MinPrice            = minPrice,
                MaxPrice            = maxPrice,
                CurrentPage         = page,
                TotalCount          = total,
                TotalPages          = (int)Math.Ceiling(total / (double)PageSize),
                AvailableCategories = await _db.Categories.OrderBy(c => c.Name).ToListAsync(),
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
                .Include(p => p.Variants).ThenInclude(v => v.AttributeValues).ThenInclude(av => av.Attribute)
                .FirstOrDefaultAsync(p => p.Id == id);
            if (product == null) return NotFound();

            var vm = new AdminProductEditViewModel
            {
                Id               = product.Id,
                Name             = product.Name,
                Slug             = product.Slug,
                Sku              = product.Sku,
                ProductType      = string.IsNullOrWhiteSpace(product.ProductType) ? "simple" : product.ProductType,
                Description      = product.Description ?? "",
                ShortDescription = product.ShortDescription,
                Price            = product.Price,
                Colour           = product.Metal,
                Weight           = product.Weight,
                IsActive         = product.IsActive,
                IsFeatured       = product.IsFeatured,
                IsNewArrival     = product.IsNewArrival,
                ExternalCode     = product.ExternalCode,
                CategoryId       = product.CategoryId,
                Categories       = await _db.Categories.OrderBy(c => c.Name).ToListAsync(),
                Images           = product.Images.OrderBy(i => i.SortOrder).ToList(),
                AllAttributes    = await _db.ProductAttributes
                                     .Include(a => a.Values.OrderBy(v => v.SortOrder))
                                     .Where(a => a.IsActive)
                                     .OrderBy(a => a.SortOrder).ThenBy(a => a.Name)
                                     .ToListAsync(),
                Variants = product.Variants.OrderBy(v => v.Name).Select(v => new AdminVariantViewModel
                {
                    Id              = v.Id,
                    Name            = v.Name,
                    Sku             = v.Sku,
                    PriceAdjustment = v.PriceAdjustment,
                    StockQuantity   = v.StockQuantity,
                    IsActive        = v.IsActive,
                    AttributeLabels = v.AttributeValues
                                       .OrderBy(av => av.Attribute.SortOrder)
                                       .Select(av => av.Value).ToList()
                }).ToList()
            };

            return View(vm);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> GenerateVariants(int id, List<int> selectedValueIds)
        {
            var product = await _db.Products
                .Include(p => p.Variants).ThenInclude(v => v.AttributeValues)
                .FirstOrDefaultAsync(p => p.Id == id);
            if (product == null) return NotFound();

            // Group selected values by attribute
            var selectedValues = await _db.ProductAttributeValues
                .Include(v => v.Attribute)
                .Where(v => selectedValueIds.Contains(v.Id))
                .OrderBy(v => v.Attribute.SortOrder).ThenBy(v => v.SortOrder)
                .ToListAsync();

            var byAttribute = selectedValues
                .GroupBy(v => v.AttributeId)
                .Select(g => g.ToList())
                .ToList();

            if (!byAttribute.Any())
            {
                TempData["Error"] = "Select at least one attribute value to generate variants.";
                return RedirectToAction(nameof(Edit), new { id });
            }

            // Cartesian product of all attribute groups
            var combinations = CartesianProduct(byAttribute);
            int created = 0;

            foreach (var combo in combinations)
            {
                var comboIds = combo.Select(v => v.Id).OrderBy(x => x).ToList();
                // Skip if a variant with this exact combination already exists
                var exists = product.Variants.Any(v =>
                    v.AttributeValues.Select(av => av.Id).OrderBy(x => x).SequenceEqual(comboIds));
                if (exists) continue;

                var name    = string.Join(" / ", combo.Select(v => v.Value));
                var variant = new ProductVariant { ProductId = id, Name = name, IsActive = true };
                foreach (var val in combo) variant.AttributeValues.Add(val);
                _db.ProductVariants.Add(variant);
                created++;
            }

            await _db.SaveChangesAsync();
            if (created > 0)
                await LogAsync("Update", "Product", id.ToString(),
                    $"Generated {created} variant(s) for '{product.Name}'");
            TempData["Success"] = created > 0
                ? $"{created} variant(s) generated."
                : "All combinations already exist.";

            return RedirectToAction(nameof(Edit), new { id });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveAllVariants(int productId,
            Microsoft.AspNetCore.Http.IFormCollection form)
        {
            var variants = await _db.ProductVariants
                .Where(v => v.ProductId == productId)
                .ToListAsync();

            int saved = 0;
            foreach (var variant in variants)
            {
                var sku  = form[$"sku_{variant.Id}"].FirstOrDefault()?.Trim();
                var adj  = form[$"adj_{variant.Id}"].FirstOrDefault();
                // The form sends hidden=false + optional checkbox=true; last value wins
                var activeVals = form[$"active_{variant.Id}"];
                var active = activeVals.Contains("true");

                variant.Sku             = sku;
                variant.PriceAdjustment = decimal.TryParse(adj,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
                variant.IsActive        = active;
                saved++;
            }

            if (saved > 0)
            {
                await _db.SaveChangesAsync();
                await LogAsync("Update", "Product", productId.ToString(), $"Saved {saved} variant(s)");
                TempData["Success"] = $"All {saved} variant(s) saved.";
            }

            return RedirectToAction(nameof(Edit), new { id = productId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteVariant(int productId, int variantId)
        {
            var variant = await _db.ProductVariants
                .FirstOrDefaultAsync(v => v.Id == variantId && v.ProductId == productId);
            if (variant != null)
            {
                var vName = variant.Name;
                _db.ProductVariants.Remove(variant);
                await _db.SaveChangesAsync();
                await LogAsync("Update", "Product", productId.ToString(), $"Deleted variant '{vName}'");
                TempData["Success"] = "Variant deleted.";
            }
            return RedirectToAction(nameof(Edit), new { id = productId });
        }

        private static IEnumerable<List<ProductAttributeValue>> CartesianProduct(
            List<List<ProductAttributeValue>> sets)
        {
            IEnumerable<List<ProductAttributeValue>> result = new[] { new List<ProductAttributeValue>() };
            foreach (var set in sets)
                result = result.SelectMany(
                    combo => set.Select(item => combo.Concat(new[] { item }).ToList()));
            return result;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(AdminProductEditViewModel vm)
        {
            vm.Categories = await _db.Categories.OrderBy(c => c.Name).ToListAsync();

            // Required fields (the Category FK is non-nullable, so guard it explicitly).
            if (string.IsNullOrWhiteSpace(vm.Name))
                ModelState.AddModelError(nameof(vm.Name), "Name is required.");
            if (vm.CategoryId is null or 0 || !vm.Categories.Any(c => c.Id == vm.CategoryId))
                ModelState.AddModelError(nameof(vm.CategoryId), "Please select a category.");

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
            product.Sku = string.IsNullOrWhiteSpace(vm.Sku) ? null : vm.Sku.Trim();
            product.ProductType = vm.ProductType == "variable" ? "variable" : "simple";
            product.IsActive = vm.IsActive;
            product.IsFeatured = vm.IsFeatured;
            product.IsNewArrival = vm.IsNewArrival;
            product.ExternalCode = vm.ExternalCode?.Trim() ?? string.Empty;
            product.CategoryId = vm.CategoryId!.Value;
            product.UpdatedAt = DateTime.UtcNow;

            var isNew = vm.Id == 0;
            await _db.SaveChangesAsync();

            await LogAsync(isNew ? "Create" : "Update", "Product", product.Id.ToString(),
                $"{(isNew ? "Created" : "Updated")} product '{product.Name}' (₦{product.Price:N0})");

            if (isNew)
            {
                TempData["Success"] = $"Product '{product.Name}' created. You can now upload images below.";
                return RedirectToAction(nameof(Edit), new { id = product.Id });
            }

            TempData["Success"] = $"Product '{product.Name}' saved.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Duplicate(int id)
        {
            var source = await _db.Products.Include(p => p.Images).FirstOrDefaultAsync(p => p.Id == id);
            if (source == null) return NotFound();

            var baseSlug = source.Slug + "-copy";
            var slug = baseSlug;
            int n = 1;
            while (await _db.Products.AnyAsync(p => p.Slug == slug))
                slug = $"{baseSlug}-{n++}";

            var copy = new Product
            {
                Name             = source.Name + " (Copy)",
                Slug             = slug,
                ExternalCode     = $"COPY-{Guid.NewGuid():N}"[..20],
                Description      = source.Description,
                ShortDescription = source.ShortDescription,
                Price            = source.Price,
                Metal            = source.Metal,
                Weight           = source.Weight,
                CategoryId       = source.CategoryId,
                IsActive         = false,
                IsFeatured       = false,
                IsNewArrival     = source.IsNewArrival,
                CreatedAt        = DateTime.UtcNow,
                UpdatedAt        = DateTime.UtcNow,
            };

            foreach (var img in source.Images.OrderBy(i => i.SortOrder))
                copy.Images.Add(new ProductImage { Url = img.Url, AltText = img.AltText, IsPrimary = img.IsPrimary, SortOrder = img.SortOrder });

            _db.Products.Add(copy);
            await _db.SaveChangesAsync();

            await LogAsync("Create", "Product", copy.Id.ToString(),
                $"Duplicated '{source.Name}' → '{copy.Name}'");

            TempData["Success"] = $"Product duplicated as '{copy.Name}'. It is inactive — edit and activate when ready.";
            return RedirectToAction(nameof(Edit), new { id = copy.Id });
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

            await LogAsync("Update", "Product", product.Id.ToString(),
                $"Set '{product.Name}' to {(product.IsActive ? "active" : "inactive")}");

            TempData["Success"] = $"'{product.Name}' is now {(product.IsActive ? "active" : "inactive")}.";
            return RedirectToAction(nameof(Index));
        }

        // ── Bulk actions on selected products ────────────────────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Bulk(string op, int[] ids,
            string q = "", string category = "", string status = "", string type = "", int page = 1)
        {
            var back = new { q, category, status, type, page };
            ids ??= Array.Empty<int>();
            if (ids.Length == 0)
            {
                TempData["Error"] = "No products were selected.";
                return RedirectToAction(nameof(Index), back);
            }

            var products = await _db.Products.Where(p => ids.Contains(p.Id)).ToListAsync();
            var n = products.Count;

            switch (op)
            {
                case "activate":   products.ForEach(p => { p.IsActive = true;   p.UpdatedAt = DateTime.UtcNow; }); break;
                case "deactivate": products.ForEach(p => { p.IsActive = false;  p.UpdatedAt = DateTime.UtcNow; }); break;
                case "feature":    products.ForEach(p => { p.IsFeatured = true; p.UpdatedAt = DateTime.UtcNow; }); break;
                case "unfeature":  products.ForEach(p => { p.IsFeatured = false;p.UpdatedAt = DateTime.UtcNow; }); break;
                case "delete":     _db.Products.RemoveRange(products); break;
                default:
                    TempData["Error"] = "Unknown bulk action.";
                    return RedirectToAction(nameof(Index), back);
            }

            await _db.SaveChangesAsync();
            await LogAsync(op == "delete" ? "Delete" : "Update", "Product", null, $"Bulk {op} on {n} product(s)");
            TempData["Success"] = $"{op} applied to {n} product(s).";
            return RedirectToAction(nameof(Index), back);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestFormLimits(MultipartBodyLengthLimit = 52428800)] // 50 MB
        public async Task<IActionResult> ImportFromWooCommerce(Microsoft.AspNetCore.Http.IFormFile csvFile)
        {
            if (csvFile == null || csvFile.Length == 0)
            {
                TempData["Error"] = "Please select a CSV file to upload.";
                return RedirectToAction(nameof(Index));
            }

            if (!csvFile.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Only .csv files are supported.";
                return RedirectToAction(nameof(Index));
            }

            try
            {
                using var stream = csvFile.OpenReadStream();
                var result = await _wooImporter.ImportFromCsvAsync(stream);
                await LogAsync("Import", "Product", null,
                    $"WooCommerce CSV import ({csvFile.FileName}): {result.Summary}");
                TempData[result.Errors.Any() ? "Warning" : "Success"] =
                    $"WooCommerce import complete: {result.Summary}" +
                    (result.Errors.Any() ? $" — First error: {result.Errors[0]}" : "");
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Import failed: {ex.Message}";
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
                var name = product.Name;
                _db.Products.Remove(product);
                await _db.SaveChangesAsync();
                await LogAsync("Delete", "Product", id.ToString(), $"Deleted product '{name}'");
                TempData["Success"] = $"Product '{name}' deleted.";
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddImage(int id, IFormFile? imageFile, string? imageUrl, string? altText, bool isPrimary)
        {
            var product = await _db.Products.FindAsync(id);
            if (product == null) return NotFound();

            // Resolve image URL: file upload takes priority over URL text field
            string resolvedUrl;
            if (imageFile != null && imageFile.Length > 0)
            {
                var ext = Path.GetExtension(imageFile.FileName).ToLowerInvariant();
                var dir = Path.Combine(_env.WebRootPath, "uploads", "products");
                Directory.CreateDirectory(dir);
                var fileName = $"{Guid.NewGuid():N}{ext}";
                await using var stream = System.IO.File.Create(Path.Combine(dir, fileName));
                await imageFile.CopyToAsync(stream);
                resolvedUrl = $"/uploads/products/{fileName}";
            }
            else if (!string.IsNullOrWhiteSpace(imageUrl))
            {
                resolvedUrl = imageUrl.Trim();
            }
            else
            {
                TempData["Error"] = "Please upload a file or provide an image URL.";
                return RedirectToAction(nameof(Edit), new { id });
            }

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
                Url = resolvedUrl,
                AltText = altText?.Trim(),
                IsPrimary = isPrimary,
                SortOrder = maxSort + 1
            });

            await _db.SaveChangesAsync();
            await LogAsync("Update", "Product", id.ToString(), $"Added image to '{product.Name}'");
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
                await LogAsync("Update", "Product", productId.ToString(), "Removed a product image");
                TempData["Success"] = "Image removed.";
            }
            return RedirectToAction(nameof(Edit), new { id = productId });
        }
    }
}
