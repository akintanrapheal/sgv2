using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")]
    public abstract class AdminBaseController : Controller
    {
        /// <summary>
        /// Records an admin action to the audit log. Resolves the audit service from the
        /// request scope so derived controllers don't need to inject it explicitly.
        /// Never throws — audit failures must not block the actual operation.
        /// </summary>
        protected async Task LogAsync(string action, string entityType, string? entityId, string description)
        {
            try
            {
                var audit = HttpContext.RequestServices.GetRequiredService<IAuditService>();
                await audit.LogAsync(action, entityType, entityId, description);
            }
            catch
            {
                // Swallow — auditing is best-effort and must never break the operation.
            }
        }
    }
}
