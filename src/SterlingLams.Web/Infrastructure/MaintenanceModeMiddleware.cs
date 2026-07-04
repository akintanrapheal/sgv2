using System.Security.Claims;
using SterlingLams.Web.Areas.Admin;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// When the <c>store.maintenance_mode</c> setting is on, shows a maintenance page to the public
/// storefront. Staff (admin/operations/etc.) and the admin/inventory/till areas keep working so the
/// shop can be managed while the front is down. Static assets, auth and webhooks stay reachable.
/// </summary>
public class MaintenanceModeMiddleware
{
    private readonly RequestDelegate _next;
    public MaintenanceModeMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx, ISettingsService settings)
    {
        var path = ctx.Request.Path.Value ?? string.Empty;

        if (IsExempt(path) || IsStaff(ctx.User) || !await settings.GetBoolAsync("store.maintenance_mode", false))
        {
            await _next(ctx);
            return;
        }

        var siteName = await settings.GetAsync("general.site_name", "Sterlin Glams");
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        ctx.Response.Headers.RetryAfter = "3600";
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(Page(siteName));
    }

    // Areas/paths that must keep working so staff can manage the shop + the page can render.
    private static bool IsExempt(string path)
    {
        bool Starts(string p) => path.StartsWith(p, StringComparison.OrdinalIgnoreCase);
        return Starts($"/{StaffPaths.Admin}") || Starts($"/{StaffPaths.Inventory}") || Starts($"/{StaffPaths.Marketing}") || Starts("/Till") || Starts("/Pos")
            || Starts("/Account/Login") || Starts("/Account/Logout") || Starts("/Account/AccessDenied")
            || Starts("/webhooks")
            || Starts("/css") || Starts("/js") || Starts("/lib") || Starts("/uploads")
            || Starts("/favicon");
    }

    private static bool IsStaff(ClaimsPrincipal user)
    {
        if (user?.Identity?.IsAuthenticated != true) return false;
        if (user.IsInRole(AdminSections.AdminRole)) return true;
        return AdminSections.DefaultStaffRoles.Any(user.IsInRole);
    }

    private static string Page(string siteName) => $@"<!doctype html>
<html lang=""en""><head><meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>{System.Net.WebUtility.HtmlEncode(siteName)} — Back soon</title>
<style>
  body{{margin:0;height:100vh;display:flex;align-items:center;justify-content:center;
       font-family:Georgia,'Times New Roman',serif;background:#0a0a0a;color:#f5f5f4;text-align:center;padding:24px;}}
  .wrap{{max-width:520px}} h1{{font-weight:300;font-size:34px;letter-spacing:1px;margin:0 0 16px}}
  p{{color:#a8a29e;font-size:15px;line-height:1.7;margin:0}}
  .brand{{font-size:12px;letter-spacing:3px;text-transform:uppercase;color:#78716c;margin-bottom:28px}}
</style></head>
<body><div class=""wrap"">
  <div class=""brand"">{System.Net.WebUtility.HtmlEncode(siteName)}</div>
  <h1>We'll be right back</h1>
  <p>Our boutique is briefly closed for updates. Please check back shortly — thank you for your patience.</p>
</div></body></html>";
}
