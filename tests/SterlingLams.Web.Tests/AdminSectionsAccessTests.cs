using System.Security.Claims;
using SterlingLams.Web.Areas.Admin;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>Locks the RBAC posture after making Users & Roles delegable: they're grantable sections and
/// reachable by a configured user-admin, while the crown-jewel screens stay owner-only.</summary>
public class AdminSectionsAccessTests
{
    private static ClaimsPrincipal UserWith(string? email)
    {
        if (email == null) return new ClaimsPrincipal(new ClaimsIdentity());   // unauthenticated
        var id = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Email, email), new Claim(ClaimTypes.Name, email) }, "test");
        return new ClaimsPrincipal(id);
    }

    [Fact]
    public void Users_and_Roles_are_grantable_sections()
    {
        var keys = AdminSections.All.ConvertAll(s => s.Key);
        Assert.Contains("Users", keys);
        Assert.Contains("Roles", keys);
    }

    [Fact]
    public void Owner_only_screens_are_not_grantable()
    {
        var keys = AdminSections.All.ConvertAll(s => s.Key);
        Assert.DoesNotContain("Integrations", keys);   // payment & SMTP keys
        Assert.DoesNotContain("Subscribe", keys);      // billing
        Assert.DoesNotContain("DataReset", keys);      // destructive reset
    }

    [Fact]
    public void IsUserAdmin_is_owner_or_configured_user_admin_only()
    {
        Assert.True(AdminSections.IsUserAdmin(UserWith("rapheal@sterlinglamslogistics.com"))); // owner
        Assert.True(AdminSections.IsUserAdmin(UserWith("abm@sterlinglams.com")));              // configured user-admin
        Assert.False(AdminSections.IsUserAdmin(UserWith("someone@else.com")));
        Assert.False(AdminSections.IsUserAdmin(UserWith(null)));
        // A user-admin is NOT the owner/super-admin (so can't reach Integrations/Subscribe/Reset).
        Assert.False(AdminSections.IsOwner(UserWith("abm@sterlinglams.com")));
        Assert.False(AdminSections.IsSuperAdmin(UserWith("abm@sterlinglams.com")));
    }
}
