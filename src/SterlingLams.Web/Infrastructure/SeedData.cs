using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Seeds the database with the 3 Sterling Lams Lagos stores,
/// product categories, and the Admin role.
/// Safe to run repeatedly — checks for existing data before inserting.
/// </summary>
public static class SeedData
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<ApplicationDbContext>>();

        try
        {
            // ─── Roles ───────────────────────────────────────────────────────
            foreach (var role in new[] { "Admin", "Customer" })
            {
                if (!await roleManager.RoleExistsAsync(role))
                {
                    await roleManager.CreateAsync(new IdentityRole(role));
                    logger.LogInformation("Created role: {Role}", role);
                }
            }

            // ─── Categories ──────────────────────────────────────────────────
            var categories = new[]
            {
                new { Name = "Rings",      Slug = "rings",      Description = "Engagement, wedding, and fashion rings" },
                new { Name = "Necklaces",  Slug = "necklaces",  Description = "Pendants, chains, and statement necklaces" },
                new { Name = "Earrings",   Slug = "earrings",   Description = "Studs, hoops, and drop earrings" },
                new { Name = "Bracelets",  Slug = "bracelets",  Description = "Bangles, cuffs, and tennis bracelets" },
                new { Name = "Brooches",   Slug = "brooches",   Description = "Lapel pins and decorative brooches" },
                new { Name = "Watches",    Slug = "watches",    Description = "Luxury timepieces and watch collections" },
                new { Name = "Sets",       Slug = "sets",       Description = "Matching jewellery sets and gift collections" },
            };

            foreach (var cat in categories)
            {
                if (!await db.Categories.AnyAsync(c => c.Slug == cat.Slug))
                {
                    db.Categories.Add(new Category
                    {
                        Name = cat.Name,
                        Slug = cat.Slug,
                        Description = cat.Description,
                        IsActive = true
                    });
                }
            }

            await db.SaveChangesAsync();

            // ─── Stores ──────────────────────────────────────────────────────
            // OdooWarehouseId matches appsettings.json Odoo:Stores mapping
            var stores = new[]
            {
                new Store
                {
                    Name            = "Victoria Island",
                    Slug            = "victoria-island",
                    Address         = "15 Adeola Odeku Street",
                    City            = "Victoria Island",
                    State           = "Lagos",
                    Phone           = "+234 1 234 5678",
                    Email           = "vi@sterlinglams.com",
                    OpeningHours    = "Mon–Sat: 10am–7pm",
                    OdooWarehouseId = 1,
                    IsActive        = true,
                    Latitude        = 6.4281,
                    Longitude       = 3.4219
                },
                new Store
                {
                    Name            = "Ikeja",
                    Slug            = "ikeja",
                    Address         = "7 Allen Avenue, Ikeja",
                    City            = "Ikeja",
                    State           = "Lagos",
                    Phone           = "+234 1 234 5679",
                    Email           = "ikeja@sterlinglams.com",
                    OpeningHours    = "Mon–Sat: 10am–7pm",
                    OdooWarehouseId = 2,
                    IsActive        = true,
                    Latitude        = 6.6085,
                    Longitude       = 3.3521
                },
                new Store
                {
                    Name            = "Lekki",
                    Slug            = "lekki",
                    Address         = "4 Admiralty Way, Lekki Phase 1",
                    City            = "Lekki",
                    State           = "Lagos",
                    Phone           = "+234 1 234 5680",
                    Email           = "lekki@sterlinglams.com",
                    OpeningHours    = "Mon–Sat: 10am–7pm, Sun: 12pm–5pm",
                    OdooWarehouseId = 3,
                    IsActive        = true,
                    Latitude        = 6.4369,
                    Longitude       = 3.4776
                }
            };

            foreach (var store in stores)
            {
                if (!await db.Stores.AnyAsync(s => s.Slug == store.Slug))
                {
                    db.Stores.Add(store);
                    logger.LogInformation("Seeded store: {Store}", store.Name);
                }
            }

            await db.SaveChangesAsync();
            logger.LogInformation("Database seeding complete.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred while seeding the database.");
            throw;
        }
    }
}
