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
            // A cancelled request — the visitor navigated away / closed the tab before it finished, so the
            // request is aborted and the in-flight DB query throws OperationCanceledException — is NOT a
            // customer-visible error (nobody saw a broken page). These were flooding the Customer Issues
            // monitor (thousands of "Query was cancelled" rows), drowning the real signal. Skip them; still
            // re-throw so the framework handles the aborted request as usual.
            var clientCancelled = ex is OperationCanceledException && context.RequestAborted.IsCancellationRequested;
            if (!clientCancelled && IsCustomerFacing(context))
                await _issues.LogAsync(StorefrontIssueType.ServerError, "unhandled_error",
                    ex.GetType().Name + ": " + ex.Message, context: context);
            throw;   // let the normal error handling + Sentry take over, unchanged
        }

        // Add-to-cart rejected (antiforgery/bad request). The storefront JS self-heals the common
        // stale/shared-token case by refetching a fresh token and replaying the POST once, tagged
        // "X-AF-Retry". We only record the issue when THAT retry also 400s — i.e. a fresh token was
        // still rejected, so the shopper genuinely saw "couldn't add to bag" (an in-app browser that
        // blocks the cookie, or a bot). First-attempt 400s that the client transparently recovers from
        // are expected and no longer flood the monitor.
        var path = context.Request.Path.Value ?? "";
        if (context.Response.StatusCode == 400 &&
            path.StartsWith("/Cart/Add", StringComparison.OrdinalIgnoreCase) &&
            context.Request.Headers["X-AF-Retry"] == "1")
        {
            await _issues.LogAsync(StorefrontIssueType.AddToCart, "add_rejected_400",
                "Add-to-cart failed even after refreshing the security token — the shopper's browser is " +
                "likely blocking the antiforgery cookie (in-app browser), or this is an automated client.",
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
