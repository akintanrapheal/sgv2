using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    public class CategoriesController : AdminBaseController
    {
        private readonly ApplicationDbContext _db;

        public CategoriesController(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task<IActionResult> Index()
        {
            ViewData["Title"] = "Categories";
            var categories = await _db.Categories
                .Include(c => c.Parent)
                .OrderBy(c => c.SortOrder)
                .ThenBy(c => c.Name)
                .ToListAsync();
            return View(categories);
        }

        public async Task<IActionResult> Create()
        {
            ViewData["Title"] = "New Category";
            var vm = new AdminCategoryEditViewModel
            {
                IsActive = true,
                AllCategories = await _db.Categories.OrderBy(c => c.Name).ToListAsync()
            };
            return View("Edit", vm);
        }

        public async Task<IActionResult> Edit(int id)
        {
            ViewData["Title"] = "Edit Category";
            var cat = await _db.Categories.FindAsync(id);
            if (cat == null) return NotFound();

            var vm = new AdminCategoryEditViewModel
            {
                Id = cat.Id,
                Name = cat.Name,
                Slug = cat.Slug,
                Description = cat.Description,
                IsActive = cat.IsActive,
                SortOrder = cat.SortOrder,
                ParentId = cat.ParentId,
                AllCategories = await _db.Categories
                    .Where(c => c.Id != id)
                    .OrderBy(c => c.Name)
                    .ToListAsync()
            };
            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(AdminCategoryEditViewModel vm)
        {
            vm.AllCategories = await _db.Categories
                .Where(c => c.Id != vm.Id)
                .OrderBy(c => c.Name)
                .ToListAsync();

            if (!ModelState.IsValid)
            {
                ViewData["Title"] = vm.Id == 0 ? "New Category" : "Edit Category";
                return View("Edit", vm);
            }

            Category category;
            if (vm.Id == 0)
            {
                category = new Category();
                _db.Categories.Add(category);
            }
            else
            {
                category = await _db.Categories.FindAsync(vm.Id) ?? new Category();
            }

            category.Name = vm.Name.Trim();
            category.Slug = string.IsNullOrWhiteSpace(vm.Slug)
                ? Regex.Replace(vm.Name.ToLower().Trim(), @"[^a-z0-9]+", "-")
                : vm.Slug.Trim();
            category.Description = vm.Description;
            category.IsActive = vm.IsActive;
            category.SortOrder = vm.SortOrder;
            category.ParentId = vm.ParentId;

            await _db.SaveChangesAsync();
            TempData["Success"] = $"Category '{category.Name}' saved.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var cat = await _db.Categories.Include(c => c.Products).FirstOrDefaultAsync(c => c.Id == id);
            if (cat == null) return NotFound();

            if (cat.Products.Any())
            {
                TempData["Error"] = "Cannot delete a category that has products assigned to it.";
                return RedirectToAction(nameof(Index));
            }

            _db.Categories.Remove(cat);
            await _db.SaveChangesAsync();
            TempData["Success"] = $"Category '{cat.Name}' deleted.";
            return RedirectToAction(nameof(Index));
        }
    }
}
