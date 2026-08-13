using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    public class UsersController : AdminBaseController
    {
        // Section == null → full administrators only. User & role management is owner-only.
        protected override string? Section => null;

        // Owner is view-only here: only Admin + Developer may create/edit users or change roles.
        public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var m = context.HttpContext.Request.Method;
            var isWrite = m == "POST" || m == "PUT" || m == "DELETE" || m == "PATCH";
            if (isWrite && !AdminSections.IsSystemManager(User))
            {
                context.Result = RedirectToAction("AccessDenied", "Account", new { area = "" });
                return;
            }
            await base.OnActionExecutionAsync(context, next);
        }

        private readonly ApplicationDbContext _db;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly IEmailService _email;
        private readonly ISettingsService _settings;
        private const int PageSize = 30;

        public UsersController(ApplicationDbContext db, UserManager<ApplicationUser> userManager,
            RoleManager<IdentityRole> roleManager, IEmailService email, ISettingsService settings)
        {
            _db = db;
            _userManager = userManager;
            _roleManager = roleManager;
            _email = email;
            _settings = settings;
        }

        // Determines a user's single display role (first backend role, else "Customer")
        private static string PrimaryRole(IList<string> roles) =>
            roles.FirstOrDefault(r => r != "Customer") ?? "Customer";

        /// <summary>
        /// True when the target account IS the signed-in one - the guard behind "you can't lock,
        /// revoke, delete or re-role yourself". Compares the user id from the auth cookie, never
        /// Email against <c>User.Identity.Name</c> (the username): those match for staff today but
        /// have drifted apart on this system before, and if they drift again these guards stop
        /// firing and an admin can lock themselves out of production.
        /// </summary>
        private bool IsSelf(ApplicationUser user) =>
            user.Id == User.FindFirstValue(ClaimTypes.NameIdentifier);

        /// <summary>The configured owner (super-admin) account — protected from being deleted, locked,
        /// demoted or role-changed by ANYONE, including other admins.</summary>
        private static bool IsOwnerAccount(ApplicationUser user) =>
            AdminSections.IsOwnerEmail(user.Email) || AdminSections.IsOwnerEmail(user.UserName);

        public async Task<IActionResult> Index(string q = "", string role = "", string status = "", int page = 1)
        {
            ViewData["Title"] = "User Management";

            var adminIds = (await _userManager.GetUsersInRoleAsync("Admin")).Select(u => u.Id).ToHashSet();

            // Backend (staff) roles — everything except the implicit Customer role.
            var staffRoles = await _roleManager.Roles
                .Where(r => r.Name != "Customer")
                .OrderBy(r => r.Name == "Admin" ? 0 : 1).ThenBy(r => r.Name)
                .Select(r => r.Name!)
                .ToListAsync();

            // This screen lists STAFF & ADMINS only — customers live in the Customers tab. Build the
            // set of everyone holding any backend role and restrict the whole page to them.
            var staffIds = new HashSet<string>();
            foreach (var r in staffRoles)
                foreach (var u in await _userManager.GetUsersInRoleAsync(r))
                    staffIds.Add(u.Id);

            var query = _db.Users.Where(u => staffIds.Contains(u.Id));

            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(u =>
                    EF.Functions.ILike(u.FirstName + " " + u.LastName, $"%{q}%") ||
                    EF.Functions.ILike(u.Email!, $"%{q}%") ||
                    EF.Functions.ILike(u.PhoneNumber ?? "", $"%{q}%"));

            // Role filter narrows within staff (e.g. only "Sales" or only "Admin").
            if (!string.IsNullOrWhiteSpace(role))
            {
                var inRole = (await _userManager.GetUsersInRoleAsync(role)).Select(u => u.Id).ToHashSet();
                query = query.Where(u => inRole.Contains(u.Id));
            }

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

            var rows = new List<AdminUserRow>();
            foreach (var u in pageUsers)
            {
                var stat = orderStats.FirstOrDefault(s => s.UserId == u.Id);
                var userRoles = await _userManager.GetRolesAsync(u);
                rows.Add(new AdminUserRow
                {
                    Id             = u.Id,
                    FullName       = u.FullName,
                    Email          = u.Email ?? "",
                    Phone          = u.PhoneNumber,
                    RoleName       = PrimaryRole(userRoles),
                    IsAdmin        = adminIds.Contains(u.Id),
                    IsLocked       = u.LockoutEnd.HasValue && u.LockoutEnd > now,
                    IsRevoked      = u.AccessRevoked,
                    EmailConfirmed = u.EmailConfirmed,
                    OrderCount     = stat?.Count ?? 0,
                    TotalSpend     = stat?.Spend ?? 0,
                    JoinedAt       = u.CreatedAt,
                    LastLoginAt    = u.LastLoginAt,
                });
            }

            // Stat cards (whole-table aggregates)
            var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var vm = new AdminUserListViewModel
            {
                Users          = rows,
                SearchQuery    = q,
                RoleFilter     = role,
                StatusFilter   = status,
                AvailableRoles = staffRoles,
                // Assignable per user: staff roles (never Admin) + Customer (removes backend access).
                // Admin included — it's a full-access role (assignable like the Create form). Only
                // Admins/Developers can reach this action at all (see OnActionExecutionAsync).
                AssignableRoles = staffRoles.Append("Customer").ToList(),
                CurrentPage    = page,
                TotalPages     = (int)Math.Ceiling(total / (double)PageSize),
                TotalCount     = total,
                TotalUsers     = staffIds.Count,
                AdminCount     = adminIds.Count,
                CustomerCount  = staffIds.Count - adminIds.Count,   // repurposed → non-admin staff ("Staff" card)
                LockedCount    = await _db.Users.CountAsync(u => u.LockoutEnd != null && u.LockoutEnd > now && staffIds.Contains(u.Id)),
                NewThisMonth   = await _db.Users.CountAsync(u => u.CreatedAt >= monthStart && staffIds.Contains(u.Id)),
            };

            return View(vm);
        }

        // ── Create new staff/admin user ────────────────────────────────────────
        // Staff roles a new user can be given (never Admin — full access isn't grantable here).
        private static string[] StaffRoles => SterlingLams.Web.Areas.Admin.AdminSections.DefaultStaffRoles;

        [HttpGet]
        public IActionResult Create()
        {
            ViewData["Title"] = "New User";
            ViewBag.Roles = StaffRoles;
            return View(new AdminCreateUserViewModel { Role = StaffRoles.First() });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(AdminCreateUserViewModel vm)
        {
            ViewData["Title"] = "New User";
            ViewBag.Roles = StaffRoles;

            if (string.IsNullOrWhiteSpace(vm.Email))
            {
                TempData["Error"] = "Email is required.";
                return View(vm);
            }
            // When not inviting, the admin must supply the password themselves.
            if (!vm.SendInvite && string.IsNullOrWhiteSpace(vm.Password))
            {
                TempData["Error"] = "Enter a password, or choose to email an invite instead.";
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

            // Invite flow: create the account with a throwaway strong password, then email a set-password
            // link so the user chooses their own (nothing to share by hand).
            var initialPassword = vm.SendInvite ? $"{Guid.NewGuid():N}Aa1!" : vm.Password;
            var result = await _userManager.CreateAsync(user, initialPassword);
            if (!result.Succeeded)
            {
                TempData["Error"] = string.Join(" ", result.Errors.Select(e => e.Description));
                return View(vm);
            }

            // Every created user is staff: give them a backend role (which also lets them shop the
            // storefront). Full "Admin" is never assignable here.
            var role = StaffRoles.Contains(vm.Role) ? vm.Role : StaffRoles.First();
            await _userManager.AddToRoleAsync(user, role);

            if (vm.SendInvite)
            {
                var sent = await SendSetPasswordInviteAsync(user);
                await LogAsync("Create", "User", user.Id, $"Created staff account {user.Email} ({role}); invite {(sent ? "emailed" : "FAILED to send")}");
                TempData[sent ? "Success" : "Error"] = sent
                    ? $"User {user.Email} created as {role}. An invite to set their password has been emailed."
                    : $"User {user.Email} created as {role}, but the invite email could not be sent (check SMTP). Use “Reset password” on the Users page to give them access.";
                return RedirectToAction(nameof(Index));
            }

            await LogAsync("Create", "User", user.Id, $"Created staff account {user.Email} ({role})");
            TempData["Success"] = $"User {user.Email} created as {role}. They can sign in to the backend and the storefront.";
            return RedirectToAction(nameof(Index));
        }

        // Emails a "set your password" link (a password-reset token) using the customizable
        // "staff_invite" template. Returns false if the send failed.
        private async Task<bool> SendSetPasswordInviteAsync(ApplicationUser user)
        {
            try
            {
                var token = await _userManager.GeneratePasswordResetTokenAsync(user);
                var link = Url.Action("ResetPassword", "Account",
                    new { area = "", token, email = user.Email }, protocol: Request.Scheme)!;

                var subject = await _settings.GetAsync("email.staff_invite.subject", "You've been added to Sterlin Glams — set your password");
                var intro = await _settings.GetAsync("email.staff_invite.intro",
                    "You've been given backend access to Sterlin Glams. Click below to set your password, then sign in with your email.");
                intro = intro.Replace("{name}", System.Net.WebUtility.HtmlEncode(user.FirstName ?? ""));

                var body = $@"
                    <h2 style=""font-size:18px;margin:0 0 16px;"">Set your password</h2>
                    <p>{System.Net.WebUtility.HtmlEncode(intro)}</p>
                    <p style=""margin:28px 0;"">
                        <a href=""{link}"" style=""background:#0a0a0a;color:#ffffff;text-decoration:none;padding:12px 28px;display:inline-block;font-size:13px;letter-spacing:1px;text-transform:uppercase;"">Set your password</a>
                    </p>
                    <p style=""font-size:13px;color:#78716c;"">This link expires shortly. If you weren't expecting this, you can ignore this email.</p>";
                return await _email.SendAsync(user.Email!, subject, body);
            }
            catch { return false; }
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

        // ── Edit a user's details (name, email, optional new password) ─────────
        [HttpGet]
        public async Task<IActionResult> Edit(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();
            if (IsSelf(user))
                return RedirectToAction("Index", "MyAccount", new { area = "" }); // edit your own on /me
            ViewData["Title"] = "Edit User";
            ViewBag.Role = (await _userManager.GetRolesAsync(user)).FirstOrDefault(r => r != "Customer") ?? "Customer";
            return View(user);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string id, string firstName, string lastName, string email, string? newPassword)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();
            if (IsSelf(user))
            {
                TempData["Error"] = "Edit your own details from the account menu.";
                return RedirectToAction(nameof(Index));
            }

            email = (email ?? "").Trim();
            if (string.IsNullOrWhiteSpace(email))
            {
                TempData["Error"] = "Email is required.";
                return RedirectToAction(nameof(Edit), new { id });
            }

            // Email = username; keep them in sync (Identity re-normalises). Block duplicates.
            if (!string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase))
            {
                var dupe = await _userManager.FindByEmailAsync(email);
                if (dupe != null && dupe.Id != user.Id)
                {
                    TempData["Error"] = "Another account already uses that email.";
                    return RedirectToAction(nameof(Edit), new { id });
                }
                var r1 = await _userManager.SetUserNameAsync(user, email);
                var r2 = await _userManager.SetEmailAsync(user, email);
                if (!r1.Succeeded || !r2.Succeeded)
                {
                    TempData["Error"] = string.Join(" ", r1.Errors.Concat(r2.Errors).Select(e => e.Description));
                    return RedirectToAction(nameof(Edit), new { id });
                }
                user.EmailConfirmed = true;
            }

            user.FirstName = (firstName ?? "").Trim();
            user.LastName = (lastName ?? "").Trim();
            await _userManager.UpdateAsync(user);

            if (!string.IsNullOrWhiteSpace(newPassword))
            {
                if (newPassword.Length < 8)
                {
                    TempData["Error"] = "Name/email saved, but the new password must be at least 8 characters.";
                    return RedirectToAction(nameof(Edit), new { id });
                }
                var token = await _userManager.GeneratePasswordResetTokenAsync(user);
                var pr = await _userManager.ResetPasswordAsync(user, token, newPassword);
                if (!pr.Succeeded)
                {
                    TempData["Error"] = "Name/email saved, but password change failed: " + string.Join(" ", pr.Errors.Select(e => e.Description));
                    return RedirectToAction(nameof(Edit), new { id });
                }
            }

            await LogAsync("Update", "User", user.Id, $"Edited details for {user.Email}");
            TempData["Success"] = $"{user.Email} updated.";
            return RedirectToAction(nameof(Index));
        }

        // ── Set / clear a till PIN for this user ───────────────────────────────
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SetPin(string id, string pin)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            pin = (pin ?? "").Trim();
            if (pin.Length == 0)
            {
                user.PinHash = null; // clear — user can no longer sign in at a till
                await _userManager.UpdateAsync(user);
                await LogAsync("Update", "User", user.Id, $"Cleared till PIN for {user.Email}");
                TempData["Success"] = $"Till PIN removed for {user.Email}.";
                return RedirectToAction(nameof(Index));
            }
            if (pin.Length < 4 || pin.Length > 8 || !pin.All(char.IsDigit))
            {
                TempData["Error"] = "PIN must be 4–8 digits.";
                return RedirectToAction(nameof(Index));
            }

            user.PinHash = _userManager.PasswordHasher.HashPassword(user, pin);
            await _userManager.UpdateAsync(user);
            await LogAsync("Update", "User", user.Id, $"Set till PIN for {user.Email}");
            TempData["Success"] = $"Till PIN set for {user.Email}.";
            return RedirectToAction(nameof(Index));
        }

        // ── CSV export ─────────────────────────────────────────────────────────
        // Exports exactly what this screen lists: STAFF and administrators. It used to dump every
        // user - so every customer's email and phone left with it - and label everyone who wasn't an
        // Admin as "Customer", which mislabelled all real staff. Roles are now the ones actually held.
        public async Task<IActionResult> ExportCsv()
        {
            var now = DateTimeOffset.UtcNow;

            // Everyone holding a backend role, with the role(s) they hold.
            var staffRoles = await _roleManager.Roles.Where(r => r.Name != "Customer")
                .Select(r => r.Name!).ToListAsync();
            var rolesByUserId = new Dictionary<string, List<string>>();
            foreach (var r in staffRoles)
                foreach (var u in await _userManager.GetUsersInRoleAsync(r))
                {
                    if (!rolesByUserId.TryGetValue(u.Id, out var list))
                        rolesByUserId[u.Id] = list = new List<string>();
                    list.Add(r);
                }

            var staffIds = rolesByUserId.Keys.ToHashSet();
            var users = await _db.Users.Where(u => staffIds.Contains(u.Id))
                .OrderByDescending(u => u.CreatedAt).ToListAsync();

            var sb = new StringBuilder();
            Csv.AppendRow(sb, "Full Name", "Email", "Phone", "Role", "Status", "Joined", "Last Login");
            foreach (var u in users)
            {
                var roles = rolesByUserId.TryGetValue(u.Id, out var rs) ? rs : new List<string>();
                var status = u.AccessRevoked ? "Revoked"
                    : u.LockoutEnd.HasValue && u.LockoutEnd > now ? "Locked"
                    : "Active";
                Csv.AppendRow(sb, u.FullName, u.Email, u.PhoneNumber,
                    string.Join(" / ", roles.OrderBy(r => r == "Admin" ? 0 : 1).ThenBy(r => r)),
                    status,
                    u.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd"),
                    u.LastLoginAt.HasValue ? u.LastLoginAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "Never");
            }

            await LogAsync("Export", "User", null, $"Exported {users.Count} staff account(s) to CSV");

            return File(Csv.ToBytes(sb), "text/csv", $"staff_{DateTime.UtcNow:yyyyMMdd}.csv");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SetRole(string id, string role)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            if (IsOwnerAccount(user))
            {
                TempData["Error"] = "The super-admin account is protected — its role can't be changed.";
                return RedirectToAction(nameof(Index));
            }
            if (IsSelf(user))
            {
                TempData["Error"] = "You cannot change your own role.";
                return RedirectToAction(nameof(Index));
            }

            role = (role ?? "Customer").Trim();

            // Validate the target role exists (Customer = no backend role)
            if (role != "Customer" && !await _roleManager.RoleExistsAsync(role))
            {
                TempData["Error"] = $"Role '{role}' does not exist.";
                return RedirectToAction(nameof(Index));
            }

            // Replace all current roles with the chosen one (Customer = none).
            var current = await _userManager.GetRolesAsync(user);
            if (current.Any())
                await _userManager.RemoveFromRolesAsync(user, current);

            if (role != "Customer")
                await _userManager.AddToRoleAsync(user, role);

            // Invalidate any live session so the user immediately loses access to the previous role
            // and must sign in again under the new one.
            await _userManager.UpdateSecurityStampAsync(user);

            await LogAsync("Update", "User", user.Id, $"Set role of {user.Email} to {role}");
            TempData["Success"] = $"{user.Email} is now {role}. Any active session has been signed out.";
            return RedirectToAction(nameof(Index));
        }

        // ── Revoke / restore access ───────────────────────────────────────────
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Revoke(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();
            if (IsOwnerAccount(user))
            {
                TempData["Error"] = "The super-admin account is protected and can't be revoked.";
                return RedirectToAction(nameof(Index));
            }
            if (IsSelf(user))
            {
                TempData["Error"] = "You cannot revoke your own access.";
                return RedirectToAction(nameof(Index));
            }
            user.AccessRevoked = true;
            await _userManager.UpdateAsync(user);
            await _userManager.UpdateSecurityStampAsync(user); // kicks out any live session
            await LogAsync("Update", "User", user.Id, $"Revoked access for {user.Email}");
            TempData["Success"] = $"{user.Email}'s access has been revoked — they can no longer sign in.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Restore(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();
            user.AccessRevoked = false;
            await _userManager.UpdateAsync(user);
            await LogAsync("Update", "User", user.Id, $"Restored access for {user.Email}");
            TempData["Success"] = $"{user.Email}'s access has been restored.";
            return RedirectToAction(nameof(Index));
        }

        // ── Delete a user (admin-only; safe — never orphans order history) ────────
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            if (IsOwnerAccount(user))
            {
                TempData["Error"] = "The super-admin account is protected and can't be deleted.";
                return RedirectToAction(nameof(Index));
            }
            if (IsSelf(user))
            {
                TempData["Error"] = "You cannot delete your own account.";
                return RedirectToAction(nameof(Index));
            }
            if (await _userManager.IsInRoleAsync(user, "Admin"))
            {
                TempData["Error"] = "Administrator accounts cannot be deleted.";
                return RedirectToAction(nameof(Index));
            }

            // Never orphan order history — block deletion and suggest revoking instead.
            var orders = await _db.Orders.CountAsync(o => o.UserId == id || o.CustomerUserId == id);
            if (orders > 0)
            {
                TempData["Error"] = $"{user.Email} has {orders} order(s) on record — revoke their access instead of deleting, to keep the history.";
                return RedirectToAction(nameof(Index));
            }

            try
            {
                var result = await _userManager.DeleteAsync(user);
                if (!result.Succeeded)
                {
                    TempData["Error"] = "Could not delete: " + string.Join("; ", result.Errors.Select(e => e.Description));
                    return RedirectToAction(nameof(Index));
                }
                await LogAsync("Delete", "User", id, $"Deleted user {user.Email}");
                TempData["Success"] = $"{user.Email} has been deleted.";
            }
            catch
            {
                TempData["Error"] = "Could not delete this account — it's still linked to other records. Revoke their access instead.";
            }
            return RedirectToAction(nameof(Index));
        }

        // ── Store-level access (writes-only) ──────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> Stores(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();
            ViewBag.User = user;
            ViewBag.AllStores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
            ViewBag.Assigned = (await _db.UserStores.Where(us => us.UserId == id)
                .Select(us => us.StoreId).ToListAsync()).ToHashSet();
            return View();
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SetStores(string id, int[] storeIds)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            var validStoreIds = (await _db.Stores.Where(s => s.IsActive).Select(s => s.Id).ToListAsync()).ToHashSet();
            var desired = (storeIds ?? Array.Empty<int>()).Where(validStoreIds.Contains).Distinct().ToList();

            var existing = await _db.UserStores.Where(us => us.UserId == id).ToListAsync();
            _db.UserStores.RemoveRange(existing);
            foreach (var sid in desired)
                _db.UserStores.Add(new UserStore { UserId = id, StoreId = sid });
            await _db.SaveChangesAsync();

            await LogAsync("Update", "User", id, desired.Count == 0
                ? $"Cleared branch access for {user.Email} (now unrestricted — all branches)"
                : $"Set branch access for {user.Email}: {desired.Count} branch(es)");

            TempData["Success"] = "Branch access updated.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleLock(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            if (IsOwnerAccount(user))
            {
                TempData["Error"] = "The super-admin account is protected and can't be locked.";
                return RedirectToAction(nameof(Index));
            }
            if (IsSelf(user))
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
