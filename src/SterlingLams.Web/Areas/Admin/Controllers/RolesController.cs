using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers;

public class RolesController : AdminBaseController
{
    // Section == null → only full Administrators can manage roles (privilege-escalation guard)
    protected override string? Section => null;

    // Roles & Permissions is super-admin-only: only the owner account can create, edit or delete roles
    // (the base controller already blocks non-super-admins since Section == null; this is belt-and-braces
    // for writes).
    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var m = context.HttpContext.Request.Method;
        var isWrite = m == "POST" || m == "PUT" || m == "DELETE" || m == "PATCH";
        if (isWrite && !AdminSections.IsSuperAdmin(User))
        {
            context.Result = RedirectToAction("AccessDenied", "Account", new { area = "" });
            return;
        }
        await base.OnActionExecutionAsync(context, next);
    }

    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IPermissionService _perms;
    private readonly ApplicationDbContext _db;

    // Built-in roles: their PERMISSIONS are editable by the super admin (so Admin/Owner/Developer can be
    // restricted), but they cannot be renamed or deleted (code references them by name). "Customer" is
    // not a backend role and is never editable here.
    private static readonly string[] LockedRoles = { "Admin", "Owner", "Developer", "Customer" };
    private static bool IsCustomer(string? name) => string.Equals(name, "Customer", StringComparison.OrdinalIgnoreCase);

    public RolesController(
        RoleManager<IdentityRole> roleManager,
        UserManager<ApplicationUser> userManager,
        IPermissionService perms,
        ApplicationDbContext db)
    {
        _roleManager = roleManager;
        _userManager = userManager;
        _perms = perms;
        _db = db;
    }

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Roles & Permissions";

        var roles = await _roleManager.Roles.OrderBy(r => r.Name).ToListAsync();
        var rows = new List<AdminRoleRow>();

        foreach (var role in roles)
        {
            var name = role.Name ?? "";
            if (name == "Customer") continue; // not a backend role

            var usersInRole = await _userManager.GetUsersInRoleAsync(name);
            // Every role (Admin included) is now permission-driven, so the summary reflects the actual
            // granted sections. Collapse granular grants ("Orders", "Orders:manage", "Settings:General")
            // to distinct base-section labels.
            var sections = (await _perms.GetRoleSectionsAsync(name))
                .Select(key => { var i = key.IndexOf(':'); return i < 0 ? key : key[..i]; })
                .Distinct()
                .Select(baseKey => AdminSections.All.FirstOrDefault(s => s.Key == baseKey)?.Label ?? baseKey)
                .ToList();

            rows.Add(new AdminRoleRow
            {
                Name = name,
                IsSystem = LockedRoles.Contains(name),   // built-in → not deletable/renamable
                CanEdit = !IsCustomer(name),             // permissions editable (Admin/Owner/Developer too)
                IsFullAccess = false,                    // no role is unconditionally full any more — only the super admin
                UserCount = usersInRole.Count,
                Sections = sections,
            });
        }

        return View(new AdminRoleListViewModel { Roles = rows });
    }

    public IActionResult Create()
    {
        ViewData["Title"] = "New Role";
        return View("Edit", new AdminRoleEditViewModel { IsNew = true });
    }

    public async Task<IActionResult> Edit(string id)
    {
        ViewData["Title"] = "Edit Role";

        if (string.IsNullOrEmpty(id) || IsCustomer(id))
        {
            TempData["Error"] = "That role cannot be edited.";
            return RedirectToAction(nameof(Index));
        }

        if (!await _roleManager.RoleExistsAsync(id)) return NotFound();

        return View(new AdminRoleEditViewModel
        {
            Name = id,
            OriginalName = id,
            IsNew = false,
            SelectedSections = await _perms.GetRoleSectionsAsync(id),
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(AdminRoleEditViewModel vm, List<string> sections)
    {
        var name = vm.Name?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["Error"] = "Role name is required.";
            return RedirectToAction(nameof(Create));
        }

        // Customer isn't a backend role; built-in roles can't be created-over or renamed, but their
        // permissions ARE editable (that's the whole point of this change).
        if (IsCustomer(name))
        {
            TempData["Error"] = "That role cannot be edited.";
            return RedirectToAction(nameof(Index));
        }
        if (vm.IsNew && LockedRoles.Contains(name))
        {
            TempData["Error"] = "That role name is reserved.";
            return RedirectToAction(nameof(Index));
        }
        if (!vm.IsNew && LockedRoles.Contains(vm.OriginalName ?? "") && !name.Equals(vm.OriginalName, StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = "Built-in roles can't be renamed — you can still change their permissions.";
            return RedirectToAction(nameof(Edit), new { id = vm.OriginalName });
        }

        if (vm.IsNew)
        {
            if (await _roleManager.RoleExistsAsync(name))
            {
                TempData["Error"] = $"A role named '{name}' already exists.";
                return RedirectToAction(nameof(Create));
            }
            var created = await _roleManager.CreateAsync(new IdentityRole(name));
            if (!created.Succeeded)
            {
                TempData["Error"] = "Could not create the role: " + string.Join(" ", created.Errors.Select(e => e.Description));
                return RedirectToAction(nameof(Create));
            }
            await LogAsync("Create", "Role", null, $"Created role '{name}'");
        }
        else if (vm.OriginalName != name)
        {
            // Rename: update the Identity role, then carry its permission rows over.
            // Both guards matter — the rename used to move the permission rows even when the rename
            // itself failed (the UpdateAsync result was ignored), which left the role under its old
            // name with no permissions at all.
            if (await _roleManager.RoleExistsAsync(name))
            {
                TempData["Error"] = $"A role named '{name}' already exists — pick another name.";
                return RedirectToAction(nameof(Edit), new { id = vm.OriginalName });
            }

            var role = await _roleManager.FindByNameAsync(vm.OriginalName);
            if (role == null)
            {
                TempData["Error"] = $"The role '{vm.OriginalName}' no longer exists.";
                return RedirectToAction(nameof(Index));
            }

            role.Name = name;
            var renamed = await _roleManager.UpdateAsync(role);
            if (!renamed.Succeeded)
            {
                TempData["Error"] = "Could not rename the role: " + string.Join(" ", renamed.Errors.Select(e => e.Description));
                return RedirectToAction(nameof(Edit), new { id = vm.OriginalName });
            }

            // Move permission rows to the new name only now that the rename has stuck.
            var perms = await _db.RolePermissions.Where(rp => rp.RoleName == vm.OriginalName).ToListAsync();
            foreach (var p in perms) p.RoleName = name;
            await _db.SaveChangesAsync();
            _perms.ClearCache();   // cached grants are keyed by role name
            await LogAsync("Update", "Role", null, $"Renamed role '{vm.OriginalName}' to '{name}'");
        }

        await _perms.SetRoleSectionsAsync(name, sections ?? new List<string>());
        await LogAsync("Update", "Role", null,
            $"Set '{name}' permissions: {(sections?.Any() == true ? string.Join(", ", sections) : "none")}");

        TempData["Success"] = $"Role '{name}' saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(string id)
    {
        if (LockedRoles.Contains(id))
        {
            TempData["Error"] = "Built-in roles cannot be deleted.";
            return RedirectToAction(nameof(Index));
        }

        var role = await _roleManager.FindByNameAsync(id);
        if (role == null) return NotFound();

        var usersInRole = await _userManager.GetUsersInRoleAsync(id);
        if (usersInRole.Any())
        {
            TempData["Error"] = $"Cannot delete '{id}' — {usersInRole.Count} user(s) still have this role. Reassign them first.";
            return RedirectToAction(nameof(Index));
        }

        // Remove permission rows then the role
        var perms = await _db.RolePermissions.Where(rp => rp.RoleName == id).ToListAsync();
        _db.RolePermissions.RemoveRange(perms);
        await _db.SaveChangesAsync();
        await _roleManager.DeleteAsync(role);
        _perms.ClearCache();

        await LogAsync("Delete", "Role", null, $"Deleted role '{id}'");
        TempData["Success"] = $"Role '{id}' deleted.";
        return RedirectToAction(nameof(Index));
    }
}
