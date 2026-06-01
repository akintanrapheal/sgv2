using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services.Inventory;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    public class InventoryController : AdminBaseController
    {
        private readonly ApplicationDbContext _db;
        private readonly IInventoryService _inventory;

        public InventoryController(ApplicationDbContext db, IInventoryService inventory)
        {
            _db = db;
            _inventory = inventory;
        }

        public async Task<IActionResult> Index()
        {
            ViewData["Title"] = "Inventory";

            var stores = await _db.Stores
                .Where(s => s.IsActive)
                .OrderBy(s => s.Name)
                .ToListAsync();

            var allInventory = await _db.StoreInventories
                .Include(si => si.Product)
                .Where(si => si.Product.IsActive)
                .ToListAsync();

            var sections = stores.Select(store => new InventoryStoreSection
            {
                Store = store,
                Products = allInventory
                    .Where(si => si.StoreId == store.Id)
                    .OrderBy(si => si.Product.Name)
                    .Select(si => new InventoryProductRow
                    {
                        ProductId = si.ProductId,
                        ProductName = si.Product.Name,
                        Sku = si.Product.Sku ?? "",
                        QuantityOnHand = si.QuantityOnHand
                    })
                    .ToList()
            }).ToList();

            var lastSync = await _db.StoreInventories
                .MaxAsync(si => (DateTime?)si.LastSyncedAt);

            var vm = new AdminInventoryViewModel
            {
                Stores = sections,
                LastSyncedAt = lastSync
            };

            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Sync()
        {
            try
            {
                await _inventory.SyncAllAsync();
                TempData["Success"] = "Inventory synced from Odoo.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Sync failed: {ex.Message}";
            }

            return RedirectToAction(nameof(Index));
        }
    }
}
