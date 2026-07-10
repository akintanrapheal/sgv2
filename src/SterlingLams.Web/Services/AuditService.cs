using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

public interface IAuditService
{
    /// <summary>Records an admin action with the current user, IP, and timestamp. Pass
    /// <paramref name="changes"/> (e.g. built with <see cref="AuditChanges"/>) to capture a
    /// before/after snapshot for Update actions. Pass <paramref name="performedBy"/> to attribute the
    /// entry to something other than the signed-in user (e.g. "API System" for automated actions).</summary>
    Task LogAsync(string action, string entityType, string? entityId, string description, string? changes = null, string? performedBy = null);
}

/// <summary>Builds a compact "Field: old → new" before/after snapshot, skipping unchanged fields.
/// Returns null when nothing changed.</summary>
public static class AuditChanges
{
    public static string? Build(params (string Field, object? Old, object? New)[] fields)
    {
        var lines = new List<string>();
        foreach (var (field, oldVal, newVal) in fields)
        {
            var o = Fmt(oldVal);
            var n = Fmt(newVal);
            if (!string.Equals(o, n, StringComparison.Ordinal))
                lines.Add($"{field}: {(string.IsNullOrEmpty(o) ? "—" : o)} → {(string.IsNullOrEmpty(n) ? "—" : n)}");
        }
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static string Fmt(object? v) => v switch
    {
        null => "",
        bool b => b ? "yes" : "no",
        decimal d => d.ToString("0.##"),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm"),
        _ => v.ToString() ?? ""
    };
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

    public async Task LogAsync(string action, string entityType, string? entityId, string description, string? changes = null, string? performedBy = null)
    {
        var ctx  = _http.HttpContext;
        var user = string.IsNullOrWhiteSpace(performedBy) ? await ResolvePerformerAsync(ctx) : performedBy.Trim();
        var ip   = GetClientIp(ctx);

        _db.AuditLogs.Add(new AuditLog
        {
            Action      = action,
            EntityType  = entityType,
            EntityId    = entityId ?? "",
            Description = description.Length > 1000 ? description[..1000] : description,
            Changes     = string.IsNullOrWhiteSpace(changes) ? null : (changes.Length > 4000 ? changes[..4000] : changes),
            PerformedBy = user,
            IpAddress   = ip,
            CreatedAt   = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync();
    }

    /// <summary>The staff member's display name (First Last) when signed in, else their username,
    /// else "system" (background jobs / unauthenticated).</summary>
    private async Task<string> ResolvePerformerAsync(HttpContext? ctx)
    {
        var id = ctx?.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrEmpty(id))
        {
            var u = await _db.Users.Where(x => x.Id == id)
                .Select(x => new { x.FirstName, x.LastName, x.UserName })
                .FirstOrDefaultAsync();
            if (u != null)
            {
                var name = $"{u.FirstName} {u.LastName}".Trim();
                return string.IsNullOrWhiteSpace(name) ? (u.UserName ?? "unknown") : name;
            }
        }
        return ctx?.User?.Identity?.Name ?? "system";
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
