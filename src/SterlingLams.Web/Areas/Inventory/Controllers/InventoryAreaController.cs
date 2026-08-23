using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

/// <summary>
/// Base for the dedicated Inventory System (its own /Inventory area + layout).
/// Access is gated on the "Inventory" section permission: VIEW to read, MANAGE to write.
/// The legacy "Inventory" role and full administrators keep full (manage) access; any other
/// role granted "Inventory:view" gets read-only access (look, but can't change anything).
/// </summary>
[Area("Inventory")]
[Authorize]   // must be signed in; section view/manage is enforced in OnActionExecutionAsync
public abstract class InventoryAreaController : Controller
{
    /// <summary>Write requests (POST/PUT/DELETE/PATCH) require "Inventory:manage"; reads need view.
    /// A controller with its own finer gate can override this to false.</summary>
    protected virtual bool EnforceManageOnWrite => true;
    /// <summary>Staff members for "Staff member" pickers — users in any backend role (i.e. NOT just
    /// a Customer), excluding guest shells. Keeps customers out of staff dropdowns project-wide.
    /// Returns anonymous { id, name } objects (consumed as dynamic in views / serialized to JSON).</summary>
    protected async Task<List<object>> StaffOptionsAsync()
    {
        var db = HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
        var customerRoleId = await db.Roles.Where(r => r.Name == "Customer").Select(r => r.Id).FirstOrDefaultAsync();
        var rows = await db.Users
            .Where(u => !u.IsGuest && db.UserRoles.Any(ur => ur.UserId == u.Id && ur.RoleId != customerRoleId))
            .OrderBy(u => u.FirstName).ThenBy(u => u.LastName)
            .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email })
            .ToListAsync();
        return rows.Select(u => (object)new
        {
            id = u.Id,
            name = ($"{u.FirstName} {u.LastName}").Trim() != "" ? $"{u.FirstName} {u.LastName}".Trim() : u.Email
        }).ToList();
    }

    /// <summary>True when the user is a real staff member (backend role, not a customer/guest).</summary>
    protected async Task<bool> IsStaffAsync(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        var db = HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
        var customerRoleId = await db.Roles.Where(r => r.Name == "Customer").Select(r => r.Id).FirstOrDefaultAsync();
        return await db.Users.AnyAsync(u => u.Id == userId && !u.IsGuest
            && db.UserRoles.Any(ur => ur.UserId == u.Id && ur.RoleId != customerRoleId));
    }

    /// <summary>Display name (full name, else email) for a staff member id.</summary>
    protected async Task<string?> StaffNameAsync(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;
        var db = HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
        return await db.Users.Where(u => u.Id == userId)
            .Select(u => ($"{u.FirstName} {u.LastName}").Trim() != "" ? ($"{u.FirstName} {u.LastName}").Trim() : u.Email)
            .FirstOrDefaultAsync();
    }

    /// <summary>Records an action to the audit log. Best-effort — never throws.</summary>
    protected async Task LogAsync(string action, string entityType, string? entityId, string description)
    {
        try
        {
            var audit = HttpContext.RequestServices.GetRequiredService<IAuditService>();
            await audit.LogAsync(action, entityType, entityId, description);
        }
        catch { /* auditing must never break the operation */ }
    }

    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // ── Access control (Inventory section: view to read, manage to write) ───────────────
        var perms = HttpContext.RequestServices.GetRequiredService<IPermissionService>();
        // Full admins and the legacy Inventory role always have full (manage) access.
        var canManage = SterlingLams.Web.Areas.Admin.AdminSections.IsFullAccess(User)
                        || User.IsInRole("Inventory")
                        || await perms.CanManageAsync(User, "Inventory");
        var canView = canManage || await perms.CanAccessAsync(User, "Inventory");
        if (!canView)
        {
            context.Result = RedirectToAction("AccessDenied", "Account", new { area = "" });
            return;
        }
        var method = context.HttpContext.Request.Method;
        var isWrite = method == "POST" || method == "PUT" || method == "DELETE" || method == "PATCH";
        if (isWrite && EnforceManageOnWrite && !canManage)
        {
            // View-only staff: block every write. 403 works for the AJAX endpoints; the write UI is
            // also hidden via CanManageInventory so form posts don't normally reach here.
            context.Result = new StatusCodeResult(403);
            return;
        }
        ViewData["CanManageInventory"] = canManage;

        var db = HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
        ViewData["PendingTransfersCount"] = await db.StockTransfers.CountAsync(
            t => t.Status == TransferStatus.PendingApproval || t.Status == TransferStatus.InTransit);
        // Approved refunds whose returned items still need a restock / write-off decision (Step 2).
        ViewData["PendingReturnsCount"] = await db.Refunds.CountAsync(
            r => r.Status == RefundStatus.Approved && r.RestockRequested
                && r.Items.Any(i => i.RestockDecision == RestockDecision.Pending));
        // Items whose price changed and whose tag needs reprinting.
        ViewData["PendingReprintsCount"] = await db.LabelReprintQueue.CountAsync(q => q.Status == ReprintStatus.Pending);

        await next();
    }
}
