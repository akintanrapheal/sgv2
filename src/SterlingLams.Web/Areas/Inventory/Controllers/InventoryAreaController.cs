using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

/// <summary>
/// Base for the dedicated Inventory System (its own /Inventory area + layout).
/// Restricted to the Inventory team and full Administrators. This is a self-contained
/// workspace (stock, transfers, till, stock-take) separate from the website admin.
/// </summary>
[Area("Inventory")]
[Authorize(Roles = "Admin,Inventory")]
public abstract class InventoryAreaController : Controller
{
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
}
