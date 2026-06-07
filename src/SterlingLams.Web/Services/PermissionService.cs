using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SterlingLams.Web.Areas.Admin;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

public interface IPermissionService
{
    /// <summary>True if the user can access the given admin section.</summary>
    Task<bool> CanAccessAsync(ClaimsPrincipal user, string section);

    /// <summary>All section keys the user can access ("*" sentinel not used — Admin returns all keys).</summary>
    Task<HashSet<string>> GetAllowedSectionsAsync(ClaimsPrincipal user);

    /// <summary>Section keys granted to a single role.</summary>
    Task<HashSet<string>> GetRoleSectionsAsync(string roleName);

    /// <summary>Replaces a role's granted sections with the supplied set.</summary>
    Task SetRoleSectionsAsync(string roleName, IEnumerable<string> sections);

    void ClearCache();
}

public class PermissionService : IPermissionService
{
    private readonly ApplicationDbContext _db;
    private readonly IMemoryCache _cache;
    private const string CacheKey = "role_permissions_map";

    public PermissionService(ApplicationDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<bool> CanAccessAsync(ClaimsPrincipal user, string section)
    {
        if (user.IsInRole(AdminSections.AdminRole)) return true;   // full access
        var allowed = await GetAllowedSectionsAsync(user);
        return allowed.Contains(section);
    }

    public async Task<HashSet<string>> GetAllowedSectionsAsync(ClaimsPrincipal user)
    {
        // Admin sees everything
        if (user.IsInRole(AdminSections.AdminRole))
            return AdminSections.All.Select(s => s.Key).ToHashSet();

        var map = await GetMapAsync();
        var result = new HashSet<string>(StringComparer.Ordinal);

        // Union of all the user's roles' granted sections
        foreach (var role in user.FindAll(ClaimTypes.Role).Select(c => c.Value))
            if (map.TryGetValue(role, out var sections))
                result.UnionWith(sections);

        return result;
    }

    public async Task<HashSet<string>> GetRoleSectionsAsync(string roleName)
    {
        var map = await GetMapAsync();
        return map.TryGetValue(roleName, out var s)
            ? new HashSet<string>(s, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    public async Task SetRoleSectionsAsync(string roleName, IEnumerable<string> sections)
    {
        var existing = await _db.RolePermissions.Where(rp => rp.RoleName == roleName).ToListAsync();
        _db.RolePermissions.RemoveRange(existing);

        foreach (var section in sections.Where(AdminSections.IsValidSection).Distinct())
            _db.RolePermissions.Add(new RolePermission { RoleName = roleName, Section = section });

        await _db.SaveChangesAsync();
        ClearCache();
    }

    public void ClearCache() => _cache.Remove(CacheKey);

    /// <summary>roleName → set of section keys, cached.</summary>
    private async Task<Dictionary<string, HashSet<string>>> GetMapAsync()
    {
        if (_cache.TryGetValue(CacheKey, out Dictionary<string, HashSet<string>>? cached) && cached != null)
            return cached;

        var all = await _db.RolePermissions.ToListAsync();
        var map = all
            .GroupBy(rp => rp.RoleName)
            .ToDictionary(
                g => g.Key,
                g => g.Select(rp => rp.Section).ToHashSet(StringComparer.Ordinal));

        _cache.Set(CacheKey, map, TimeSpan.FromMinutes(10));
        return map;
    }
}
