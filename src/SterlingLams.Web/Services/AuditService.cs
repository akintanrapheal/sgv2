using Microsoft.AspNetCore.Http;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

public interface IAuditService
{
    /// <summary>Records an admin action with the current user, IP, and timestamp.</summary>
    Task LogAsync(string action, string entityType, string? entityId, string description);
}

public class AuditService : IAuditService
{
    private readonly ApplicationDbContext _db;
    private readonly IHttpContextAccessor _http;

    public AuditService(ApplicationDbContext db, IHttpContextAccessor http)
    {
        _db = db;
        _http = http;
    }

    public async Task LogAsync(string action, string entityType, string? entityId, string description)
    {
        var ctx  = _http.HttpContext;
        var user = ctx?.User?.Identity?.Name ?? "system";
        var ip   = GetClientIp(ctx);

        _db.AuditLogs.Add(new AuditLog
        {
            Action      = action,
            EntityType  = entityType,
            EntityId    = entityId ?? "",
            Description = description.Length > 1000 ? description[..1000] : description,
            PerformedBy = user,
            IpAddress   = ip,
            CreatedAt   = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync();
    }

    private static string? GetClientIp(HttpContext? ctx)
    {
        if (ctx == null) return null;

        // Respect reverse-proxy forwarded header (Frappe Cloud / nginx) if present
        var fwd = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(fwd))
            return fwd.Split(',')[0].Trim();

        return ctx.Connection.RemoteIpAddress?.ToString();
    }
}
