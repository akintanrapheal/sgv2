using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Marketing.Controllers;

/// <summary>
/// Base for the dedicated Marketing / Social hub (its own /Marketing area + layout). A
/// self-contained workspace for the Social Media / Marketing team — campaigns, audiences,
/// automations and (later) social scheduling — separate from the website Admin and Inventory
/// backends. Restricted to the Social Media team and full Administrators.
/// </summary>
[Area("Marketing")]
[Authorize(Roles = "Admin,Owner,Developer,Social Media")]
public abstract class MarketingAreaController : Controller
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
