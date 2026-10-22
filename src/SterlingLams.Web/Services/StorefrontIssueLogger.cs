using Microsoft.AspNetCore.Http;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

/// <summary>Records a customer-facing ordering problem for the Admin → Customer Issues monitor.
/// Every call is best-effort and MUST NOT throw or block the shopper — a logging failure is swallowed.</summary>
public interface IStorefrontIssueLogger
{
    Task LogAsync(StorefrontIssueType type, string reason, string? message = null,
        int? productId = null, string? productName = null,
        int? orderId = null, string? orderNumber = null,
        string? contactEmail = null, string? contactPhone = null,
        HttpContext? context = null, CancellationToken ct = default);
}

public class StorefrontIssueLogger : IStorefrontIssueLogger
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<StorefrontIssueLogger> _logger;

    public StorefrontIssueLogger(IServiceScopeFactory scopes, IHttpContextAccessor http,
        ILogger<StorefrontIssueLogger> logger)
    {
        _scopes = scopes;
        _http = http;
        _logger = logger;
    }

    public async Task LogAsync(StorefrontIssueType type, string reason, string? message = null,
        int? productId = null, string? productName = null,
        int? orderId = null, string? orderNumber = null,
        string? contactEmail = null, string? contactPhone = null,
        HttpContext? context = null, CancellationToken ct = default)
    {
        try
        {
            var ctx = context ?? _http.HttpContext;
            string? Trim(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : (s!.Length > max ? s[..max] : s);

            var issue = new StorefrontIssue
            {
                CreatedAt = DateTime.UtcNow,
                Type = type,
                Reason = Trim(reason, 80) ?? "unknown",
                Message = Trim(message, 1000),
                ProductId = productId,
                ProductName = Trim(productName, 300),
                OrderId = orderId,
                OrderNumber = Trim(orderNumber, 60),
                ContactEmail = Trim(contactEmail, 256),
                ContactPhone = Trim(contactPhone, 40),
            };

            if (ctx != null)
            {
                issue.Path = Trim(ctx.Request?.Path.Value, 300);
                issue.IpAddress = Trim(ctx.Connection?.RemoteIpAddress?.ToString(), 64);
                issue.DeviceType = Trim(
                    Infrastructure.OrderAttributionMiddleware.DeviceFromUserAgent(ctx.Request?.Headers.UserAgent.ToString() ?? ""), 40);
                if (ctx.User?.Identity?.IsAuthenticated == true)
                    issue.UserId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            }

            // Its own scope + context so saving an issue never flushes a half-built order on the request's
            // scoped DbContext (checkout is mid-transaction when some of these fire).
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.StorefrontIssues.Add(issue);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Monitoring must never break the thing it monitors.
            _logger.LogWarning(ex, "Failed to record storefront issue ({Type}/{Reason}).", type, reason);
        }
    }
}
