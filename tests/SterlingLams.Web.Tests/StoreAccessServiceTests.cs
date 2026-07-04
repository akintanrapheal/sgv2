using System.Security.Claims;
using SterlingLams.Web.Areas.Admin;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>Store-level (writes-only) authorization: Admin → all branches; explicitly assigned →
/// only those; unassigned non-admin → all (legacy). Guards stock/transfer/till writes (FX-35 / OP-8).</summary>
public class StoreAccessServiceTests
{
    private static ClaimsPrincipal Principal(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public async Task Admin_can_write_to_all_active_stores()
    {
        using var t = new TestDb();
        var (abuja, allen, ikota) = t.SeedBranches();
        var svc = new StoreAccessService(t.Db);

        var ids = await svc.WritableStoreIdsAsync(Principal("admin-1", AdminSections.AdminRole));

        Assert.Equal(new[] { abuja.Id, allen.Id, ikota.Id }.OrderBy(x => x), ids.OrderBy(x => x));
        Assert.True(await svc.CanWriteAsync(Principal("admin-1", AdminSections.AdminRole), allen.Id));
    }

    [Fact]
    public async Task Assigned_user_is_limited_to_their_branches()
    {
        using var t = new TestDb();
        var (abuja, allen, _) = t.SeedBranches();
        var user = t.SeedUser();
        t.Db.UserStores.Add(new UserStore { UserId = user.Id, StoreId = allen.Id });
        t.Db.SaveChanges();
        var svc = new StoreAccessService(t.Db);

        var ids = await svc.WritableStoreIdsAsync(Principal(user.Id));

        Assert.Equal(new[] { allen.Id }, ids.ToArray());
        Assert.True(await svc.CanWriteAsync(Principal(user.Id), allen.Id));
        Assert.False(await svc.CanWriteAsync(Principal(user.Id), abuja.Id));
    }

    [Fact]
    public async Task Unassigned_nonadmin_keeps_legacy_all_access()
    {
        using var t = new TestDb();
        t.SeedBranches();
        var user = t.SeedUser();
        var svc = new StoreAccessService(t.Db);

        var ids = await svc.WritableStoreIdsAsync(Principal(user.Id));

        Assert.Equal(3, ids.Count);
    }

    [Fact]
    public async Task Anonymous_user_can_write_nowhere()
    {
        using var t = new TestDb();
        t.SeedBranches();
        var svc = new StoreAccessService(t.Db);

        // No NameIdentifier claim → not admin, not assigned → empty set.
        var ids = await svc.WritableStoreIdsAsync(new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.Empty(ids);
    }
}
