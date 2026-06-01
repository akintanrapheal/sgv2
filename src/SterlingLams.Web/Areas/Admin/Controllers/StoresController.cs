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
    public class StoresController : AdminBaseController
    {
        private readonly ApplicationDbContext _db;

        public StoresController(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task<IActionResult> Index()
        {
            ViewData["Title"] = "Stores";
            var stores = await _db.Stores.OrderBy(s => s.Name).ToListAsync();
            return View(stores);
        }

        public async Task<IActionResult> Edit(int id)
        {
            ViewData["Title"] = "Edit Store";

            var store = await _db.Stores.FindAsync(id);
            if (store == null) return NotFound();

            var vm = new AdminStoreEditViewModel
            {
                Id = store.Id,
                Name = store.Name,
                Address = store.Address,
                City = store.City,
                State = store.State,
                Phone = store.Phone,
                Email = store.Email,
                OpeningHours = store.OpeningHours,
                OdooWarehouseId = store.OdooWarehouseId,
                IsActive = store.IsActive
            };

            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(AdminStoreEditViewModel vm)
        {
            if (!ModelState.IsValid) return View(vm);

            var store = await _db.Stores.FindAsync(vm.Id);
            if (store == null) return NotFound();

            store.Name = vm.Name.Trim();
            store.Address = vm.Address.Trim();
            store.City = vm.City.Trim();
            store.State = vm.State.Trim();
            store.Phone = vm.Phone?.Trim();
            store.Email = vm.Email?.Trim();
            store.OpeningHours = vm.OpeningHours?.Trim();
            store.OdooWarehouseId = vm.OdooWarehouseId;
            store.IsActive = vm.IsActive;

            await _db.SaveChangesAsync();

            TempData["Success"] = $"Store '{store.Name}' updated.";
            return RedirectToAction(nameof(Index));
        }
    }
}
