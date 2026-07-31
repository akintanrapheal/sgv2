using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Admin.Controllers;

public class StoresController : AdminBaseController
{
    protected override string Section => "Stores";

    private readonly ApplicationDbContext _db;

    public StoresController(ApplicationDbContext db) => _db = db;

    // ── Index ─────────────────────────────────────────────────────────────────
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Stores";
        var stores = await _db.Stores
            .Include(s => s.Inventories)
            .OrderBy(s => s.Name)
            .ToListAsync();
        return View(stores);
    }

    // ── Create ────────────────────────────────────────────────────────────────
    public IActionResult Create()
    {
        ViewData["Title"] = "New Store";
        return View("Edit", new AdminStoreEditViewModel { IsActive = true });
    }

    // ── Edit GET ──────────────────────────────────────────────────────────────
    public async Task<IActionResult> Edit(int id)
    {
        ViewData["Title"] = "Edit Store";
        var store = await _db.Stores.FindAsync(id);
        if (store == null) return NotFound();

        return View(new AdminStoreEditViewModel
        {
            Id               = store.Id,
            Name             = store.Name,
            Slug             = store.Slug,
            Address          = store.Address,
            City             = store.City,
            State            = store.State,
            Phone            = store.Phone,
            Email            = store.Email,
            OpeningHours     = store.OpeningHours,
            Latitude         = store.Latitude,
            Longitude        = store.Longitude,
            IsActive         = store.IsActive,
        });
    }

    // ── Save (create + update) ────────────────────────────────────────────────
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(AdminStoreEditViewModel vm)
    {
        if (!ModelState.IsValid)
        {
            ViewData["Title"] = vm.Id == 0 ? "New Store" : "Edit Store";
            return View("Edit", vm);
        }

        var slug = string.IsNullOrWhiteSpace(vm.Slug)
            ? Slugify(vm.Name)
            : vm.Slug.Trim().ToLowerInvariant();

        // Ensure slug is unique (exclude self on update)
        var baseSlug = slug;
        int n = 1;
        while (await _db.Stores.AnyAsync(s => s.Slug == slug && s.Id != vm.Id))
            slug = $"{baseSlug}-{n++}";

        Store store;
        if (vm.Id == 0)
        {
            store = new Store();
            _db.Stores.Add(store);
        }
        else
        {
            // A stale edit link (store since deleted) must not silently "save" nothing.
            var existing = await _db.Stores.FindAsync(vm.Id);
            if (existing == null)
            {
                TempData["Error"] = "That store no longer exists.";
                return RedirectToAction(nameof(Index));
            }
            store = existing;
        }

        store.Name             = vm.Name.Trim();
        store.Slug             = slug;
        store.Address          = vm.Address.Trim();
        store.City             = vm.City.Trim();
        store.State            = vm.State.Trim();
        store.Phone            = vm.Phone?.Trim();
        store.Email            = vm.Email?.Trim();
        store.OpeningHours     = vm.OpeningHours?.Trim();
        store.Latitude         = vm.Latitude;
        store.Longitude        = vm.Longitude;
        store.IsActive         = vm.IsActive;

        var isNew = vm.Id == 0;
        await _db.SaveChangesAsync();

        await LogAsync(isNew ? "Create" : "Update", "Store", store.Id.ToString(),
            $"{(isNew ? "Created" : "Updated")} store '{store.Name}' ({store.City}, {store.State})");

        TempData["Success"] = isNew
            ? $"Store '{store.Name}' created successfully."
            : $"Store '{store.Name}' updated.";

        return RedirectToAction(nameof(Index));
    }

    // ── Toggle Active ─────────────────────────────────────────────────────────
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(int id)
    {
        var store = await _db.Stores.FindAsync(id);
        if (store == null) return NotFound();

        store.IsActive = !store.IsActive;
        await _db.SaveChangesAsync();

        await LogAsync("Update", "Store", store.Id.ToString(),
            $"Set store '{store.Name}' to {(store.IsActive ? "active" : "inactive")}");

        TempData["Success"] = $"'{store.Name}' is now {(store.IsActive ? "active" : "inactive")}.";
        return RedirectToAction(nameof(Index));
    }

    // ── Delete ────────────────────────────────────────────────────────────────
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var store = await _db.Stores
            .Include(s => s.Inventories)
            .Include(s => s.Orders)
            .FirstOrDefaultAsync(s => s.Id == id);

        if (store == null) return NotFound();

        // A branch that has ever traded is referenced by far more than its inventory rows: the stock
        // ledger, adjustments, stock-takes, transfers, tills, parked sales and staff assignments all
        // point at it. Clearing only StoreInventories left those behind, so the delete died on a
        // foreign key and returned an error page. Name what's in the way and keep the history.
        var blockers = new List<string>();
        void Add(int n, string label) { if (n > 0) blockers.Add($"{n} {label}"); }

        Add(store.Orders.Count, "order(s)");
        Add(await _db.StockMovements.CountAsync(m => m.StoreId == id), "stock movement(s)");
        Add(await _db.StockAdjustments.CountAsync(a => a.StoreId == id), "stock adjustment(s)");
        Add(await _db.StockTakes.CountAsync(s => s.StoreId == id), "stock take(s)");
        Add(await _db.StockTransfers.CountAsync(t => t.FromStoreId == id || t.ToStoreId == id), "transfer(s)");
        Add(await _db.Registers.CountAsync(r => r.StoreId == id), "till(s)");
        Add(await _db.ParkedSales.CountAsync(p => p.StoreId == id), "parked sale(s)");
        Add(await _db.StockReservations.CountAsync(r => r.StoreId == id), "stock hold(s)");
        Add(await _db.UserStores.CountAsync(us => us.StoreId == id), "staff assignment(s)");

        if (blockers.Count > 0)
        {
            TempData["Error"] = $"Cannot delete '{store.Name}' — it still has {string.Join(", ", blockers)}. "
                + "Deactivate it instead, which hides it everywhere but keeps its history.";
            return RedirectToAction(nameof(Index));
        }

        // Nothing but (empty) inventory rows left — safe to remove those and the store.
        var name = store.Name;
        _db.StoreInventories.RemoveRange(store.Inventories);
        _db.Stores.Remove(store);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            TempData["Error"] = $"'{name}' is still referenced by other records and can't be deleted. Deactivate it instead.";
            return RedirectToAction(nameof(Index));
        }

        await LogAsync("Delete", "Store", id.ToString(), $"Deleted store '{name}'");

        TempData["Success"] = $"Store '{name}' deleted.";
        return RedirectToAction(nameof(Index));
    }

    private static string Slugify(string name) =>
        Regex.Replace(name.ToLowerInvariant().Trim(), @"[^a-z0-9]+", "-").Trim('-');
}
