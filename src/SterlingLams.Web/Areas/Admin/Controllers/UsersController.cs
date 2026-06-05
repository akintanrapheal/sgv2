using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    public class UsersController : AdminBaseController
    {
        private readonly ApplicationDbContext _db;
        private readonly UserManager<ApplicationUser> _userManager;
        private const int PageSize = 30;

        public UsersController(ApplicationDbContext db, UserManager<ApplicationUser> userManager)
        {
            _db = db;
            _userManager = userManager;
        }

        public async Task<IActionResult> Index(string q = "", int page = 1)
        {
            ViewData["Title"] = "User Management";

            var query = _db.Users.AsQueryable();

            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(u =>
                    EF.Functions.ILike(u.FirstName + " " + u.LastName, $"%{q}%") ||
                    EF.Functions.ILike(u.Email!, $"%{q}%"));

            var total = await query.CountAsync();
            var users = await query
                .OrderByDescending(u => u.CreatedAt)
                .Skip((page - 1) * PageSize)
                .Take(PageSize)
                .ToListAsync();

            // Get roles
            var userRoles = new System.Collections.Generic.Dictionary<string, bool>();
            foreach (var u in users)
            {
                var roles = await _userManager.GetRolesAsync(u);
                userRoles[u.Id] = roles.Contains("Admin");
            }

            ViewBag.UserIsAdmin = userRoles;
            ViewBag.SearchQuery = q;
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = (int)Math.Ceiling(total / (double)PageSize);

            return View(users);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleAdmin(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            // Prevent removing own admin
            if (user.Email == User.Identity?.Name)
            {
                TempData["Error"] = "You cannot remove your own admin role.";
                return RedirectToAction(nameof(Index));
            }

            if (await _userManager.IsInRoleAsync(user, "Admin"))
            {
                await _userManager.RemoveFromRoleAsync(user, "Admin");
                TempData["Success"] = $"{user.Email} is no longer an admin.";
            }
            else
            {
                await _userManager.AddToRoleAsync(user, "Admin");
                TempData["Success"] = $"{user.Email} is now an admin.";
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleLock(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            if (user.Email == User.Identity?.Name)
            {
                TempData["Error"] = "You cannot lock your own account.";
                return RedirectToAction(nameof(Index));
            }

            if (user.LockoutEnd.HasValue && user.LockoutEnd > DateTimeOffset.UtcNow)
            {
                await _userManager.SetLockoutEndDateAsync(user, null);
                TempData["Success"] = $"{user.Email} account unlocked.";
            }
            else
            {
                await _userManager.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddYears(100));
                TempData["Success"] = $"{user.Email} account locked.";
            }

            return RedirectToAction(nameof(Index));
        }
    }
}
