using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Infrastructure;

public static class RoleSeedData
{
    // Default staff roles and the sections they can access out of the box.
    private static readonly Dictionary<string, string[]> DefaultRoles = new()
    {
        ["Operations"]   = new[] { "Dashboard", "Orders", "Inventory", "Stores" },
        ["Sales"]        = new[] { "Dashboard", "Orders", "Customers", "Discounts" },
        ["Inventory"]    = new[] { "Dashboard", "Products", "Inventory", "Stores", "Categories", "Attributes" },
        ["Social Media"] = new[] { "Dashboard", "Products" },
    };

    public static async Task SeedAsync(RoleManager<IdentityRole> roleManager, ApplicationDbContext db, ILogger logger)
    {
        foreach (var (roleName, sections) in DefaultRoles)
        {
            // Create the Identity role if missing
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                await roleManager.CreateAsync(new IdentityRole(roleName));
                logger.LogInformation("Created staff role: {Role}", roleName);
            }

            // Seed default section permissions only if this role has none yet
            // (so admin edits aren't overwritten on restart)
            var hasAny = await db.RolePermissions.AnyAsync(rp => rp.RoleName == roleName);
            if (!hasAny)
            {
                foreach (var section in sections)
                    db.RolePermissions.Add(new RolePermission { RoleName = roleName, Section = section });
                logger.LogInformation("Seeded default permissions for {Role}: {Sections}",
                    roleName, string.Join(", ", sections));
            }
        }

        await db.SaveChangesAsync();
    }
}
