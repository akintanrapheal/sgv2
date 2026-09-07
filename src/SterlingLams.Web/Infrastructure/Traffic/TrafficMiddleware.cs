using System.Security.Cryptography;
using System.Text;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Infrastructure.Traffic;

/// <summary>
/// Records one storefront page view per HTML GET that returns 200 — handed off to
/// <see cref="ITrafficRecorder"/> (off the request path). Skips assets, APIs, health checks and every
/// staff/back-office path, so the Traffic dashboard reflects real customer-facing traffic. Wrapped in
/// try/catch: analytics must never affect the response.
/// </summary>
public class TrafficMiddleware
{
    private readonly RequestDelegate _next;
    public TrafficMiddleware(RequestDelegate next) => _next = next;

    public async Task Invoke(HttpContext ctx, ITrafficRecorder recorder)
    {
        await _next(ctx);
        try { Capture(ctx, recorder); } catch { /* never let tracking break a request */ }
    }

    private static void Capture(HttpContext ctx, ITrafficRecorder recorder)
    {
        if (!HttpMethods.IsGet(ctx.Request.Method)) return;
        if (ctx.Response.StatusCode != StatusCodes.Status200OK) return;

        var ct = ctx.Response.ContentType;
        if (ct == null || !ct.Contains("text/html", StringComparison.OrdinalIgnoreCase)) return;

        var path = ctx.Request.Path.Value ?? "/";
        if (IsExcluded(path)) return;

        var ua = ctx.Request.Headers.UserAgent.ToString();
        recorder.Record(new TrafficHit
        {
            CreatedAt   = DateTime.UtcNow,
            Path        = path.Length > 200 ? path[..200] : path,
            VisitorKey  = VisitorKey(ctx, ua),
            RefererHost = RefererHost(ctx),
            Device      = Device(ua),
        });
    }

    private static bool IsExcluded(string path)
    {
        var p = path.TrimStart('/');
        string[] prefixes =
        {
            "health", "api/", "site/", "webhooks/", "_", "lib/", "css/", "js/", "img/", "images/",
            "fonts/", "favicon", "robots", "sitemap", "Identity",
            Infrastructure.StaffPaths.Admin, Infrastructure.StaffPaths.Inventory,
            Infrastructure.StaffPaths.Marketing, Infrastructure.StaffPaths.Pos,
        };
        foreach (var pre in prefixes)
            if (!string.IsNullOrEmpty(pre) && p.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) return true;

        // A path segment with a file extension (…/logo.png, /app.css) is an asset, not a page.
        var last = p.Contains('/') ? p[(p.LastIndexOf('/') + 1)..] : p;
        return last.Contains('.');
    }

    private static string? RefererHost(HttpContext ctx)
    {
        var r = ctx.Request.Headers.Referer.ToString();
        if (string.IsNullOrWhiteSpace(r) || !Uri.TryCreate(r, UriKind.Absolute, out var u)) return null;
        var host = u.Host;
        if (host.Equals(ctx.Request.Host.Host, StringComparison.OrdinalIgnoreCase)) return null; // internal nav
        return host.Length > 100 ? host[..100] : host;
    }

    private static string Device(string ua)
    {
        if (string.IsNullOrEmpty(ua)) return "Desktop";
        var u = ua.ToLowerInvariant();
        if (u.Contains("bot") || u.Contains("crawl") || u.Contains("spider") || u.Contains("slurp")
            || u.Contains("facebookexternalhit") || u.Contains("bingpreview") || u.Contains("headless")) return "Bot";
        if (u.Contains("ipad") || u.Contains("tablet")) return "Tablet";
        if (u.Contains("mobi") || u.Contains("android") || u.Contains("iphone")) return "Mobile";
        return "Desktop";
    }

    // Per-day, per-visitor hash (IP+UA+day) — enough to count distinct visitors without storing any PII.
    private static string VisitorKey(HttpContext ctx, string ua)
    {
        var ip = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim();
        if (string.IsNullOrEmpty(ip)) ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
        var raw = $"{ip}|{ua}|{DateTime.UtcNow:yyyyMMdd}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes, 0, 8);
    }
}
