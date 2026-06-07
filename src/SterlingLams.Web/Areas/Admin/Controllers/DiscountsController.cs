using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    public class DiscountsController : AdminBaseController
    {
        protected override string Section => "Discounts";

        private readonly ApplicationDbContext _db;

        public DiscountsController(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task<IActionResult> Index()
        {
            ViewData["Title"] = "Discount Codes";
            var codes = await _db.DiscountCodes
                .OrderByDescending(d => d.CreatedAt)
                .ToListAsync();
            return View(new AdminDiscountListViewModel { DiscountCodes = codes });
        }

        public async Task<IActionResult> Create()
        {
            ViewData["Title"] = "New Discount Code";
            return View("Edit", new AdminDiscountEditViewModel
            {
                IsActive = true,
                Type = "Percentage",
                Scope = "EntireOrder",
                AllCategories = await _db.Categories.OrderBy(c => c.Name).ToListAsync(),
                AllProducts = await _db.Products.OrderBy(p => p.Name).ToListAsync(),
            });
        }

        public async Task<IActionResult> Edit(int id)
        {
            ViewData["Title"] = "Edit Discount Code";
            var code = await _db.DiscountCodes
                .Include(d => d.Categories)
                .Include(d => d.Products)
                .FirstOrDefaultAsync(d => d.Id == id);
            if (code == null) return NotFound();

            return View(new AdminDiscountEditViewModel
            {
                Id = code.Id,
                Code = code.Code,
                Description = code.Description,
                Type = code.Type.ToString(),
                Value = code.Value,
                Scope = code.Scope.ToString(),
                IsAutomatic = code.IsAutomatic,
                MinimumOrderAmount = code.MinimumOrderAmount,
                MinimumQuantity = code.MinimumQuantity,
                MaxUses = code.MaxUses,
                MaxUsesPerCustomer = code.MaxUsesPerCustomer,
                FirstOrderOnly = code.FirstOrderOnly,
                IsActive = code.IsActive,
                StartsAt = code.StartsAt,
                ExpiresAt = code.ExpiresAt,
                SelectedCategoryIds = code.Categories.Select(c => c.CategoryId).ToList(),
                SelectedProductIds = code.Products.Select(p => p.ProductId).ToList(),
                AllCategories = await _db.Categories.OrderBy(c => c.Name).ToListAsync(),
                AllProducts = await _db.Products.OrderBy(p => p.Name).ToListAsync(),
            });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(AdminDiscountEditViewModel vm,
            List<int> categoryIds, List<int> productIds)
        {
            if (string.IsNullOrWhiteSpace(vm.Code) && !vm.IsAutomatic)
            {
                TempData["Error"] = "A code is required (or mark it as an automatic promotion).";
                return RedirectToAction(vm.Id == 0 ? nameof(Create) : nameof(Edit), new { id = vm.Id });
            }

            DiscountCode code;
            if (vm.Id == 0)
            {
                code = new DiscountCode { CreatedAt = DateTime.UtcNow };
                _db.DiscountCodes.Add(code);
            }
            else
            {
                code = await _db.DiscountCodes
                    .Include(d => d.Categories)
                    .Include(d => d.Products)
                    .FirstOrDefaultAsync(d => d.Id == vm.Id) ?? new DiscountCode();
            }

            // Automatic promos still need a unique internal code — generate one if blank
            code.Code = string.IsNullOrWhiteSpace(vm.Code)
                ? $"AUTO-{Guid.NewGuid():N}"[..12].ToUpper()
                : vm.Code.Trim().ToUpper();
            code.Description        = vm.Description;
            code.Type              = Enum.TryParse<DiscountType>(vm.Type, out var t) ? t : DiscountType.Percentage;
            code.Value             = vm.Value;
            code.Scope             = Enum.TryParse<DiscountScope>(vm.Scope, out var sc) ? sc : DiscountScope.EntireOrder;
            code.IsAutomatic       = vm.IsAutomatic;
            code.MinimumOrderAmount = vm.MinimumOrderAmount;
            code.MinimumQuantity   = vm.MinimumQuantity;
            code.MaxUses           = vm.MaxUses;
            code.MaxUsesPerCustomer = vm.MaxUsesPerCustomer;
            code.FirstOrderOnly    = vm.FirstOrderOnly;
            code.IsActive          = vm.IsActive;
            code.StartsAt          = vm.StartsAt;
            code.ExpiresAt         = vm.ExpiresAt;

            // Reset scope targets
            code.Categories.Clear();
            code.Products.Clear();
            if (code.Scope == DiscountScope.Categories)
                foreach (var cid in (categoryIds ?? new()).Distinct())
                    code.Categories.Add(new DiscountCategory { CategoryId = cid });
            else if (code.Scope == DiscountScope.Products)
                foreach (var pid in (productIds ?? new()).Distinct())
                    code.Products.Add(new DiscountProduct { ProductId = pid });

            var isNew = vm.Id == 0;
            await _db.SaveChangesAsync();
            await LogAsync(isNew ? "Create" : "Update", "Discount", code.Id.ToString(),
                $"{(isNew ? "Created" : "Updated")} discount '{code.Code}' ({code.Type}, {code.Scope})");

            TempData["Success"] = $"Discount '{code.Code}' saved.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var code = await _db.DiscountCodes.FindAsync(id);
            if (code != null)
            {
                var name = code.Code;
                _db.DiscountCodes.Remove(code);
                await _db.SaveChangesAsync();
                await LogAsync("Delete", "Discount", id.ToString(), $"Deleted discount '{name}'");
                TempData["Success"] = $"Discount '{name}' deleted.";
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Toggle(int id)
        {
            var code = await _db.DiscountCodes.FindAsync(id);
            if (code == null) return NotFound();

            code.IsActive = !code.IsActive;
            await _db.SaveChangesAsync();
            await LogAsync("Update", "Discount", id.ToString(),
                $"Set discount '{code.Code}' to {(code.IsActive ? "active" : "inactive")}");
            TempData["Success"] = $"'{code.Code}' is now {(code.IsActive ? "active" : "inactive")}.";
            return RedirectToAction(nameof(Index));
        }

        // ── Usage view: which orders used this code ──────────────────────────────
        public async Task<IActionResult> Usage(int id)
        {
            ViewData["Title"] = "Discount Usage";
            var code = await _db.DiscountCodes.FindAsync(id);
            if (code == null) return NotFound();

            var usages = await _db.Orders
                .Include(o => o.User)
                .Where(o => o.DiscountCode == code.Code)
                .OrderByDescending(o => o.CreatedAt)
                .Select(o => new DiscountUsageRow
                {
                    OrderId = o.Id,
                    OrderNumber = o.OrderNumber,
                    CustomerName = o.User.FirstName + " " + o.User.LastName,
                    DiscountAmount = o.DiscountAmount,
                    OrderTotal = o.Total,
                    CreatedAt = o.CreatedAt,
                })
                .ToListAsync();

            return View(new AdminDiscountUsageViewModel
            {
                Discount = code,
                Usages = usages,
                TotalDiscounted = usages.Sum(u => u.DiscountAmount),
            });
        }
    }
}
