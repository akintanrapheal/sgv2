using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

        public async Task<IActionResult> Index(string q = "", string role = "", string status = "", int page = 1)
        {
            ViewData["Title"] = "User Management";

            // Admin user IDs (one role lookup for the whole admin role)
            var adminUsers = await _userManager.GetUsersInRoleAsync("Admin");
            var adminIds   = adminUsers.Select(u => u.Id).ToHashSet();

            var query = _db.Users.AsQueryable();

            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(u =>
                    EF.Functions.ILike(u.FirstName + " " + u.LastName, $"%{q}%") ||
                    EF.Functions.ILike(u.Email!, $"%{q}%") ||
                    EF.Functions.ILike(u.PhoneNumber ?? "", $"%{q}%"));

            if (role == "admin")
                query = query.Where(u => adminIds.Contains(u.Id));
            else if (role == "customer")
                query = query.Where(u => !adminIds.Contains(u.Id));

            var now = DateTimeOffset.UtcNow;
            if (status == "locked")
                query = query.Where(u => u.LockoutEnd != null && u.LockoutEnd > now);
            else if (status == "active")
                query = query.Where(u => u.LockoutEnd == null || u.LockoutEnd <= now);

            var total = await query.CountAsync();

            var pageUsers = await query
                .OrderByDescending(u => u.CreatedAt)
                .Skip((page - 1) * PageSize)
                .Take(PageSize)
                .ToListAsync();

            // Order counts + spend per user (one grouped query)
            var pageUserIds = pageUsers.Select(u => u.Id).ToList();
            var orderStats = await _db.Orders
                .Where(o => pageUserIds.Contains(o.UserId))
                .GroupBy(o => o.UserId)
                .Select(g => new
                {
                    UserId = g.Key,
                    Count  = g.Count(),
                    Spend  = g.Where(o => o.IsPaid).Sum(o => (decimal?)o.Total) ?? 0
                })
                .ToListAsync();

            var rows = pageUsers.Select(u =>
            {
                var stat = orderStats.FirstOrDefault(s => s.UserId == u.Id);
                return new AdminUserRow
                {
                    Id             = u.Id,
                    FullName       = u.FullName,
                    Email          = u.Email ?? "",
                    Phone          = u.PhoneNumber,
                    IsAdmin        = adminIds.Contains(u.Id),
                    IsLocked       = u.LockoutEnd.HasValue && u.LockoutEnd > now,
                    EmailConfirmed = u.EmailConfirmed,
                    OrderCount     = stat?.Count ?? 0,
                    TotalSpend     = stat?.Spend ?? 0,
                    JoinedAt       = u.CreatedAt,
                    LastLoginAt    = u.LastLoginAt,
                };
            }).ToList();

            // Stat cards (whole-table aggregates)
            var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var vm = new AdminUserListViewModel
            {
                Users         = rows,
                SearchQuery   = q,
                RoleFilter    = role,
                StatusFilter  = status,
                CurrentPage   = page,
                TotalPages    = (int)Math.Ceiling(total / (double)PageSize),
                TotalCount    = total,
                TotalUsers    = await _db.Users.CountAsync(),
                AdminCount    = adminIds.Count,
                CustomerCount = await _db.Users.CountAsync() - adminIds.Count,
                LockedCount   = await _db.Users.CountAsync(u => u.LockoutEnd != null && u.LockoutEnd > now),
                NewThisMonth  = await _db.Users.CountAsync(u => u.CreatedAt >= monthStart),
            };

            return View(vm);
        }

        // ── Create new staff/admin user ────────────────────────────────────────
        [HttpGet]
        public IActionResult Create()
        {
            ViewData["Title"] = "New User";
            return View(new AdminCreateUserViewModel());
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(AdminCreateUserViewModel vm)
        {
            ViewData["Title"] = "New User";

            if (string.IsNullOrWhiteSpace(vm.Email) || string.IsNullOrWhiteSpace(vm.Password))
            {
                TempData["Error"] = "Email and password are required.";
                return View(vm);
            }

            if (await _userManager.FindByEmailAsync(vm.Email) != null)
            {
                TempData["Error"] = "A user with that email already exists.";
                return View(vm);
            }

            var user = new ApplicationUser
            {
                UserName       = vm.Email.Trim(),
                Email          = vm.Email.Trim(),
                FirstName      = vm.FirstName.Trim(),
                LastName       = vm.LastName.Trim(),
                PhoneNumber    = vm.Phone?.Trim(),
                EmailConfirmed = true,
                CreatedAt      = DateTime.UtcNow,
            };

            var result = await _userManager.CreateAsync(user, vm.Password);
            if (!result.Succeeded)
            {
                TempData["Error"] = string.Join(" ", result.Errors.Select(e => e.Description));
                return View(vm);
            }

            if (vm.MakeAdmin)
                await _userManager.AddToRoleAsync(user, "Admin");

            await LogAsync("Create", "User", user.Id,
                $"Created {(vm.MakeAdmin ? "admin" : "customer")} account {user.Email}");

            TempData["Success"] = $"User {user.Email} created" + (vm.MakeAdmin ? " as admin." : ".");
            return RedirectToAction(nameof(Index));
        }

        // ── Reset password (admin sets a new one) ──────────────────────────────
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPassword(string id, string newPassword)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
            {
                TempData["Error"] = "New password must be at least 8 characters.";
                return RedirectToAction(nameof(Index));
            }

            var token  = await _userManager.GeneratePasswordResetTokenAsync(user);
            var result = await _userManager.ResetPasswordAsync(user, token, newPassword);

            if (result.Succeeded)
            {
                await LogAsync("Update", "User", user.Id, $"Reset password for {user.Email}");
                TempData["Success"] = $"Password reset for {user.Email}.";
            }
            else
            {
                TempData["Error"] = string.Join(" ", result.Errors.Select(e => e.Description));
            }

            return RedirectToAction(nameof(Index));
        }

        // ── CSV export ─────────────────────────────────────────────────────────
        public async Task<IActionResult> ExportCsv()
        {
            var adminIds = (await _userManager.GetUsersInRoleAsync("Admin")).Select(u => u.Id).ToHashSet();
            var now = DateTimeOffset.UtcNow;

            var users = await _db.Users.OrderByDescending(u => u.CreatedAt).ToListAsync();

            var sb = new StringBuilder();
            sb.AppendLine("Full Name,Email,Phone,Role,Status,Joined,Last Login");
            foreach (var u in users)
            {
                var rl = adminIds.Contains(u.Id) ? "Admin" : "Customer";
                var st = u.LockoutEnd.HasValue && u.LockoutEnd > now ? "Locked" : "Active";
                sb.AppendLine(string.Join(",",
                    $"\"{u.FullName}\"", $"\"{u.Email}\"", $"\"{u.PhoneNumber}\"",
                    rl, st, $"\"{u.CreatedAt:yyyy-MM-dd}\"",
                    $"\"{(u.LastLoginAt.HasValue ? u.LastLoginAt.Value.ToString("yyyy-MM-dd HH:mm") : "Never")}\""));
            }

            await LogAsync("Export", "User", null, $"Exported {users.Count} user(s) to CSV");

            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return File(bytes, "text/csv", $"users_{DateTime.UtcNow:yyyyMMdd}.csv");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleAdmin(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            if (user.Email == User.Identity?.Name)
            {
                TempData["Error"] = "You cannot remove your own admin role.";
                return RedirectToAction(nameof(Index));
            }

            if (await _userManager.IsInRoleAsync(user, "Admin"))
            {
                await _userManager.RemoveFromRoleAsync(user, "Admin");
                await LogAsync("Update", "User", user.Id, $"Removed Admin role from {user.Email}");
                TempData["Success"] = $"{user.Email} is no longer an admin.";
            }
            else
            {
                await _userManager.AddToRoleAsync(user, "Admin");
                await LogAsync("Update", "User", user.Id, $"Granted Admin role to {user.Email}");
                TempData["Success"] = $"{user.Email} is now an admin.";
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost, ValidateAntiForgeryToken]
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
                await LogAsync("Update", "User", user.Id, $"Unlocked account {user.Email}");
                TempData["Success"] = $"{user.Email} account unlocked.";
            }
            else
            {
                await _userManager.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddYears(100));
                await LogAsync("Update", "User", user.Id, $"Locked account {user.Email}");
                TempData["Success"] = $"{user.Email} account locked.";
            }

            return RedirectToAction(nameof(Index));
        }
    }
}
