using System;
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

        public IActionResult Create()
        {
            ViewData["Title"] = "New Discount Code";
            return View("Edit", new AdminDiscountEditViewModel { IsActive = true, Type = "Percentage" });
        }

        public async Task<IActionResult> Edit(int id)
        {
            ViewData["Title"] = "Edit Discount Code";
            var code = await _db.DiscountCodes.FindAsync(id);
            if (code == null) return NotFound();

            return View(new AdminDiscountEditViewModel
            {
                Id = code.Id,
                Code = code.Code,
                Description = code.Description,
                Type = code.Type.ToString(),
                Value = code.Value,
                MinimumOrderAmount = code.MinimumOrderAmount,
                MaxUses = code.MaxUses,
                IsActive = code.IsActive,
                StartsAt = code.StartsAt,
                ExpiresAt = code.ExpiresAt
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(AdminDiscountEditViewModel vm)
        {
            if (!ModelState.IsValid)
            {
                ViewData["Title"] = vm.Id == 0 ? "New Discount Code" : "Edit Discount Code";
                return View("Edit", vm);
            }

            DiscountCode code;
            if (vm.Id == 0)
            {
                code = new DiscountCode { CreatedAt = DateTime.UtcNow };
                _db.DiscountCodes.Add(code);
            }
            else
            {
                code = await _db.DiscountCodes.FindAsync(vm.Id) ?? new DiscountCode();
            }

            code.Code = vm.Code.Trim().ToUpper();
            code.Description = vm.Description;
            code.Type = Enum.TryParse<DiscountType>(vm.Type, out var t) ? t : DiscountType.Percentage;
            code.Value = vm.Value;
            code.MinimumOrderAmount = vm.MinimumOrderAmount;
            code.MaxUses = vm.MaxUses;
            code.IsActive = vm.IsActive;
            code.StartsAt = vm.StartsAt;
            code.ExpiresAt = vm.ExpiresAt;

            await _db.SaveChangesAsync();
            TempData["Success"] = $"Discount code '{code.Code}' saved.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var code = await _db.DiscountCodes.FindAsync(id);
            if (code != null)
            {
                _db.DiscountCodes.Remove(code);
                await _db.SaveChangesAsync();
                TempData["Success"] = $"Discount code '{code.Code}' deleted.";
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Toggle(int id)
        {
            var code = await _db.DiscountCodes.FindAsync(id);
            if (code == null) return NotFound();

            code.IsActive = !code.IsActive;
            await _db.SaveChangesAsync();
            TempData["Success"] = $"'{code.Code}' is now {(code.IsActive ? "active" : "inactive")}.";
            return RedirectToAction(nameof(Index));
        }
    }
}
