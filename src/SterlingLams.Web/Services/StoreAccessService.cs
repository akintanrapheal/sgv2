using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.Services;

/// <summary>
/// Store-level authorization (writes-only). Resolves which branches a user may MUTATE:
/// Admins → all; a user with explicit assignments → just those; a user with NO assignments →
/// all (unrestricted/legacy, so existing staff aren't locked out until an admin narrows them).
/// </summary>
public interface IStoreAccessService
{
    Task<HashSet<int>> WritableStoreIdsAsync(ClaimsPrincipal user);
    Task<bool> CanWriteAsync(ClaimsPrincipal user, int storeId);
}

public class StoreAccessService : IStoreAccessService
{
    private readonly ApplicationDbContext _db;
    public StoreAccessService(ApplicationDbContext db) => _db = db;

    public async Task<HashSet<int>> WritableStoreIdsAsync(ClaimsPrincipal user)
    {
        var allActive = await _db.Stores.Where(s => s.IsActive).Select(s => s.Id).ToListAsync();

        if (AdminSections.IsFullAccess(user)) return allActive.ToHashSet();

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return new HashSet<int>();

        var assigned = await _db.UserStores
            .Where(us => us.UserId == userId)
            .Select(us => us.StoreId)
            .ToListAsync();

        // No explicit assignment → unrestricted (legacy). Otherwise limited to assigned branches.
        return assigned.Count == 0 ? allActive.ToHashSet() : assigned.ToHashSet();
    }

    public async Task<bool> CanWriteAsync(ClaimsPrincipal user, int storeId) =>
        (await WritableStoreIdsAsync(user)).Contains(storeId);
}
