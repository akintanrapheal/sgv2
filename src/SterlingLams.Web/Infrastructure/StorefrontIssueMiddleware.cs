using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Watches customer-facing requests and records two things for the Customer Issues monitor without ever
/// changing the response: (1) an unhandled error on a storefront/checkout page (re-thrown so the normal
/// error page + Sentry still run), and (2) an add-to-cart that comes back 400 — the "couldn't add to bag"
/// antiforgery/bad-request case that otherwise leaves no trace. Logging is best-effort and never throws.
/// </summary>
public class StorefrontIssueMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IStorefrontIssueLogger _issues;

    // Staff/system areas and endpoints that aren't customer shopping — their errors aren't "customer issues".
    private static readonly string[] NonCustomer =
        { "/admin", "/inventory", "/marketing", "/pos", "/health", "/ingest", "/webhooks", "/api" };

    public StorefrontIssueMiddleware(RequestDelegate next, IStorefrontIssueLogger issues)
    {
        _next = next;
        _issues = issues;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            if (IsCustomerFacing(context))
                await _issues.LogAsync(StorefrontIssueType.ServerError, "unhandled_error",
                    ex.GetType().Name + ": " + ex.Message, context: context);
            throw;   // let the normal error handling + Sentry take over, unchanged
        }

        // Add-to-cart rejected (antiforgery/bad request) — the shopper saw "couldn't add to bag".
        var path = context.Request.Path.Value ?? "";
        if (context.Response.StatusCode == 400 &&
            path.StartsWith("/Cart/Add", StringComparison.OrdinalIgnoreCase))
        {
            await _issues.LogAsync(StorefrontIssueType.AddToCart, "add_rejected_400",
                "Add-to-cart was rejected (400 — usually a missing antiforgery token / in-app browser).",
                context: context);
        }
    }

    private static bool IsCustomerFacing(HttpContext ctx)
    {
        var p = ctx.Request.Path.Value ?? "";
        foreach (var seg in NonCustomer)
            if (p.StartsWith(seg, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
