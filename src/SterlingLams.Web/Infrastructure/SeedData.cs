using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Seeds the database with the 3 Sterlin Glams Lagos stores,
/// product categories, and the Admin role.
/// Safe to run repeatedly â€” checks for existing data before inserting.
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
            // â”€â”€â”€ Roles â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            foreach (var role in new[] { "Admin", "Customer" })
            {
                if (!await roleManager.RoleExistsAsync(role))
                {
                    await roleManager.CreateAsync(new IdentityRole(role));
                    logger.LogInformation("Created role: {Role}", role);
                }
            }

            // â”€â”€â”€ Categories â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            var categories = new[]
            {
                new { Name = "Rings",        Slug = "rings",        Description = "Engagement, wedding, and fashion rings" },
                new { Name = "Necklaces",    Slug = "necklaces",    Description = "Pendants, chains, and statement necklaces" },
                new { Name = "Earrings",     Slug = "earrings",     Description = "Studs, hoops, and drop earrings" },
                new { Name = "Bracelets",    Slug = "bracelets",    Description = "Bangles, cuffs, and tennis bracelets" },
                new { Name = "Brooches",     Slug = "brooches",     Description = "Lapel pins and decorative brooches" },
                new { Name = "Watches",      Slug = "watches",      Description = "Luxury timepieces and watch collections" },
                new { Name = "Sets",         Slug = "sets",         Description = "Matching jewellery sets and gift collections" },
                new { Name = "Clutches",     Slug = "clutches",     Description = "Evening clutches and stoned bags" },
                new { Name = "Sunglasses",   Slug = "sunglasses",   Description = "Fashion and crystal sunglasses" },
                new { Name = "Accessories",  Slug = "accessories",  Description = "Hair accessories, waist chains, scarfs, and more" },
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

            // â”€â”€â”€ Stores â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            var stores = new[]
            {
                new Store
                {
                    Name            = "Sterlin Glams Abuja",
                    Slug            = "sterlin-glams-abuja",
                    Address         = "Elboogie Place Adjacent Kilimanjaro, 3rd Avenue, Gwarinpa, Abuja",
                    City            = "Gwarimpa",
                    State           = "Abuja",
                    Phone           = "+234 1 234 5678",
                    Email           = "abuja@sterlinglams.com",
                    OpeningHours    = "Mon-Sat: 8am-8pm, Sun: 12pm-8pm",
                    IsActive        = true,
                    Latitude        = 9.0563,
                    Longitude       = 7.4985
                },
                new Store
                {
                    Name            = "Sterlin Glams Allen",
                    Slug            = "sterlin-glams-allen",
                    Address         = "47 Allen Avenue, Studio 24 Building, Opp Item7go Restaurant, Ikeja, Lagos",
                    City            = "Ikeja",
                    State           = "Lagos",
                    Phone           = "+234 1 234 5679",
                    Email           = "allen@sterlinglams.com",
                    OpeningHours    = "Mon-Sat: 8am-8pm, Sun: 12pm-8pm",
                    IsActive        = true,
                    Latitude        = 6.6085,
                    Longitude       = 3.3521
                },
                new Store
                {
                    Name            = "Sterlin Glams Ikota",
                    Slug            = "sterlin-glams-ikota",
                    Address         = "Shop J22, Ikota Shopping Complex, Ikota Ajah, Lagos",
                    City            = "Ajah",
                    State           = "Lagos",
                    Phone           = "+234 1 234 5680",
                    Email           = "ikota@sterlinglams.com",
                    OpeningHours    = "Mon-Sat: 8am-8pm, Sun: 12pm-8pm",
                    IsActive        = true,
                    Latitude        = 6.4369,
                    Longitude       = 3.5676
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

            // â”€â”€â”€ Dummy Products â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            await SeedProductsAsync(db, logger);

            logger.LogInformation("Database seeding complete.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred while seeding the database.");
            throw;
        }
    }

    private static async Task SeedProductsAsync(ApplicationDbContext db, ILogger logger)
    {
        // Idempotent â€” skip if any products already exist
        if (await db.Products.AnyAsync()) return;

        var rings     = await db.Categories.FirstOrDefaultAsync(c => c.Slug == "rings");
        var necklaces = await db.Categories.FirstOrDefaultAsync(c => c.Slug == "necklaces");
        var earrings  = await db.Categories.FirstOrDefaultAsync(c => c.Slug == "earrings");
        var bracelets = await db.Categories.FirstOrDefaultAsync(c => c.Slug == "bracelets");

        var storeAbuja = await db.Stores.FirstOrDefaultAsync(s => s.Slug == "sterlin-glams-abuja");
        var storeAllen = await db.Stores.FirstOrDefaultAsync(s => s.Slug == "sterlin-glams-allen");
        var storeIkota = await db.Stores.FirstOrDefaultAsync(s => s.Slug == "sterlin-glams-ikota");

        if (rings == null || necklaces == null || earrings == null || bracelets == null ||
            storeAbuja == null || storeAllen == null || storeIkota == null)
        {
            logger.LogWarning("Categories or stores missing â€” skipping product seed.");
            return;
        }

        var products = new List<Product>
        {
            // â”€â”€ Rings â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            new()
            {
                ExternalCode     = "SL-RING-001",
                Name             = "Diamond Solitaire Ring",
                Slug             = "diamond-solitaire-ring",
                ShortDescription = "A timeless 1.0ct round brilliant diamond set in 18K white gold.",
                Description      = "Crafted with a single, show-stopping 1.0ct round brilliant diamond of VS clarity and G colour. The classic four-prong setting in polished 18K white gold lets the stone speak for itself. Available in half-sizes 4â€“8.",
                Price            = 450_000,
                Currency         = "NGN",
                Material         = "18K White Gold",
                Metal            = "White Gold",
                GemstoneType     = "Diamond",
                Carat            = "1.0ct",
                Sku              = "SL-RING-001",
                IsActive         = true,
                IsFeatured       = true,
                IsNewArrival     = true,
                CategoryId       = rings.Id,
                Images = new List<ProductImage>
                {
                    new() { Url = "https://images.unsplash.com/photo-1605100804763-247f67b3557e?w=600&q=80", IsPrimary = true,  AltText = "Diamond Solitaire Ring" },
                    new() { Url = "https://images.unsplash.com/photo-1515562141207-7a88fb7ce338?w=600&q=80", IsPrimary = false, AltText = "Diamond Solitaire Ring â€“ side view" }
                },
                StoreInventories = new List<StoreInventory>
                {
                    new() { StoreId = storeAbuja.Id,    QuantityOnHand = 8, QuantityReserved = 1, UpdatedAt = DateTime.UtcNow },
                    new() { StoreId = storeAllen.Id, QuantityOnHand = 5, QuantityReserved = 0, UpdatedAt = DateTime.UtcNow },
                    new() { StoreId = storeIkota.Id, QuantityOnHand = 4, QuantityReserved = 0, UpdatedAt = DateTime.UtcNow }
                }
            },
            new()
            {
                ExternalCode     = "SL-RING-002",
                Name             = "18K Gold Wedding Band",
                Slug             = "18k-gold-wedding-band",
                ShortDescription = "A classic 4mm 18K yellow gold wedding band with a high-polish finish.",
                Description      = "Simplicity perfected. This 4mm plain wedding band in 18K yellow gold is finished to a mirror-bright polish. An eternal choice for the modern bride or groom. Comfort-fit interior.",
                Price            = 180_000,
                Currency         = "NGN",
                Material         = "18K Yellow Gold",
                Metal            = "Yellow Gold",
                Sku              = "SL-RING-002",
                IsActive         = true,
                IsFeatured       = false,
                IsNewArrival     = false,
                CategoryId       = rings.Id,
                Images = new List<ProductImage>
                {
                    new() { Url = "https://images.unsplash.com/photo-1515562141207-7a88fb7ce338?w=600&q=80", IsPrimary = true, AltText = "18K Gold Wedding Band" }
                },
                StoreInventories = new List<StoreInventory>
                {
                    new() { StoreId = storeAbuja.Id,    QuantityOnHand = 12, QuantityReserved = 0, UpdatedAt = DateTime.UtcNow },
                    new() { StoreId = storeAllen.Id, QuantityOnHand =  8, QuantityReserved = 0, UpdatedAt = DateTime.UtcNow },
                    new() { StoreId = storeIkota.Id, QuantityOnHand =  6, QuantityReserved = 0, UpdatedAt = DateTime.UtcNow }
                }
            },
            new()
            {
                ExternalCode     = "SL-RING-003",
                Name             = "Ruby Halo Engagement Ring",
                Slug             = "ruby-halo-engagement-ring",
                ShortDescription = "A 1.2ct Burmese ruby encircled by a diamond halo in 18K rose gold.",
                Description      = "Make a bold declaration of love. A vivid 1.2ct Burmese ruby sits at the centre of a scintillating diamond halo, all set in romantic 18K rose gold. The split-shank adds modern drama.",
                Price            = 680_000,
                Currency         = "NGN",
                Material         = "18K Rose Gold",
                Metal            = "Rose Gold",
                GemstoneType     = "Ruby",
                Carat            = "1.2ct",
                Sku              = "SL-RING-003",
                IsActive         = true,
                IsFeatured       = true,
                IsNewArrival     = false,
                CategoryId       = rings.Id,
                Images = new List<ProductImage>
                {
                    new() { Url = "https://images.unsplash.com/photo-1602751584552-8ba73aad10e1?w=600&q=80", IsPrimary = true, AltText = "Ruby Halo Engagement Ring" }
                },
                StoreInventories = new List<StoreInventory>
                {
                    new() { StoreId = storeAbuja.Id,    QuantityOnHand = 3, QuantityReserved = 0, UpdatedAt = DateTime.UtcNow },
                    new() { StoreId = storeAllen.Id, QuantityOnHand = 2, QuantityReserved = 0, UpdatedAt = DateTime.UtcNow },
                    new() { StoreId = storeIkota.Id, QuantityOnHand = 2, QuantityReserved = 0, UpdatedAt = DateTime.UtcNow }
                }
            },

            // â”€â”€ Necklaces â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            new()
            {
                ExternalCode     = "SL-NECK-001",
                Name             = "South Sea Pearl Necklace",
                Slug             = "south-sea-pearl-necklace",
                ShortDescription = "A 16-inch strand of luminous South Sea pearls with an 18K gold clasp.",
                Description      = "Exuding understated elegance, this 16-inch strand features perfectly matched South Sea pearls (9â€“10mm) with a rich, creamy orient. Finished with an 18K yellow gold barrel clasp.",
                Price            = 320_000,
                Currency         = "NGN",
                Material         = "South Sea Pearls, 18K Yellow Gold",
                Metal            = "Yellow Gold",
                GemstoneType     = "Pearl",
                Sku              = "SL-NECK-001",
                IsActive         = true,
                IsFeatured       = true,
                IsNewArrival     = false,
                CategoryId       = necklaces.Id,
                Images = new List<ProductImage>
                {
                    new() { Url = "https://images.unsplash.com/photo-1599643478518-a784e5dc4c8f?w=600&q=80", IsPrimary = true, AltText = "South Sea Pearl Necklace" }
                },
                StoreInventories = new List<StoreInventory>
                {
                    new() { StoreId = storeAbuja.Id,    QuantityOnHand = 6, QuantityReserved = 0, UpdatedAt = DateTime.UtcNow },
                    new() { StoreId = storeAllen.Id, QuantityOnHand = 4, QuantityReserved = 1, UpdatedAt = DateTime.UtcNow },
                    new() { StoreId = storeIkota.Id, QuantityOnHand = 3, QuantityReserved = 0, UpdatedAt = DateTime.UtcNow }
                }
            },
            new()
            {
                ExternalCode     = "SL-NECK-002",
                Name             = "Diamond Pendant Necklace",
                Slug             = "diamond-pendant-necklace",
                ShortDescription = "A 0.5ct pear-shaped diamond pendant on an 18-inch 18K white gold chain.",
                Description      = "Delicate yet dazzling. A pear-shaped diamond of 0.5ct (VS, G colour) is suspended from a gossamer-fine 18-inch 18K white gold box chain. Arrives in a sterling presentation box.",
                Price            = 195_000,
                Currency         = "NGN",
                Material         = "18K White Gold",
                Metal            = "White Gold",
                GemstoneType     = "Diamond",
                Carat            = "0.5ct",
                Sku              = "SL-NECK-002",
                IsActive         = true,
                IsFeatured       = false,
                IsNewArrival     = true,
                CategoryId       = necklaces.Id,
                Images = new List<ProductImage>
                {
                    new() { Url = "https://images.unsplash.com/photo-1611591437281-460bfbe1220a?w=600&q=80", IsPrimary = true, AltText = "Diamond Pendant Necklace" }
                },
                StoreInventories = new List<StoreInventory>
                {
                    new() { StoreId = storeAbuja.Id,    QuantityOnHand = 9, QuantityReserved = 0, UpdatedAt = DateTime.UtcNow },
                    new() { StoreId = storeAllen.Id, QuantityOnHand = 7, QuantityReserved = 0, UpdatedAt = DateTime.UtcNow },
                    new() { StoreId = storeIkota.Id, QuantityOnHand = 5, QuantityReserved = 0, UpdatedAt = DateTime.UtcNow }
                }
            },

            // â”€â”€ Earrings â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            new()
            {
                ExternalCode     = "SL-EARR-001",
                Name             = "Diamond Stud Earrings",
                Slug             = "diamond-stud-earrings",
                ShortDescription = "Classic 0.50ct tw round brilliant diamond studs in 18K white gold.",
                Description      = "A jewellery wardrobe essential. Each earring features a 0.25ct round brilliant diamond (0.50ct total weight) of VS clarity, set in a classic four-prong 18K white gold basket with a secure butterfly back.",
                Price            = 285_000,
                Currency         = "NGN",
                Material         = "18K White Gold",
                Metal            = "White Gold",
                GemstoneType     = "Diamond",
                Carat            = "0.50ct tw",
                Sku              = "SL-EARR-001",
                IsActive         = true,
                IsFeatured       = true,
                IsNewArrival     = false,
                CategoryId       = earrings.Id,
                Images = new List<ProductImage>
                {
                    new() { Url = "https://images.unsplash.com/photo-1589128777073-263566ae5e4d?w=600&q=80", IsPrimary = true, AltText = "Diamond Stud Earrings" }
                },
                StoreInventories = new List<StoreInventory>
                {
                    new() { StoreId = storeAbuja.Id,    QuantityOnHand = 10, QuantityReserved = 0, UpdatedAt = DateTime.UtcNow },
                    new() { StoreId = storeAllen.Id, QuantityOnHand =  7, QuantityReserved = 0, UpdatedAt = DateTime.UtcNow },
                    new() { StoreId = storeIkota.Id, QuantityOnHand =  5, QuantityReserved = 0, UpdatedAt = DateTime.UtcNow }
                }
            },
            new()
            {
                ExternalCode     = "SL-EARR-002",
                Name             = "18K Gold Hoop Earrings",
                Slug             = "18k-gold-hoop-earrings",
                ShortDescription = "Timeless 30mm hoops crafted in polished 18K yellow gold.",
                Description      = "A style staple in 18K yellow gold. These 30mm hoops have a polished exterior and brushed interior finish, perfect for day-to-night wear. Hinged closure with a secure click lock.",
                Price            = 125_000,
                Currency         = "NGN",
                Material         = "18K Yellow Gold",
                Metal            = "Yellow Gold",
                Sku              = "SL-EARR-002",
                IsActive         = true,
                IsFeatured       = false,
                IsNewArrival     = true,
                CategoryId       = earrings.Id,
                Images = new List<ProductImage>
                {
                    new() { Url = "https://images.unsplash.com/photo-1573408301185-9519f94815f3?w=600&q=80", IsPrimary = true, AltText = "18K Gold Hoop Earrings" }
                },
                StoreInventories = new List<StoreInventory>
                {
                    new() { StoreId = storeAbuja.Id,    QuantityOnHand = 15, QuantityReserved = 0, UpdatedAt = DateTime.UtcNow },
                    new() { StoreId = storeAllen.Id, QuantityOnHand = 10, QuantityReserved = 0, UpdatedAt = DateTime.UtcNow },
                    new() { StoreId = storeIkota.Id, QuantityOnHand =  8, QuantityReserved = 0, UpdatedAt = DateTime.UtcNow }
                }
            },

            // â”€â”€ Bracelets â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            new()
            {
                ExternalCode     = "SL-BRAC-001",
                Name             = "Diamond Tennis Bracelet",
                Slug             = "diamond-tennis-bracelet",
                ShortDescription = "A brilliant 3.0ct total weight diamond tennis bracelet in 18K white gold.",
                Description      = "The pinnacle of wrist adornment. This 7-inch classic tennis bracelet features 38 round brilliant diamonds totalling 3.0ct of VS clarity, each held in a four-prong 18K white gold setting with a box-clasp closure.",
                Price            = 750_000,
                Currency         = "NGN",
                Material         = "18K White Gold",
                Metal            = "White Gold",
                GemstoneType     = "Diamond",
                Carat            = "3.0ct tw",
                Sku              = "SL-BRAC-001",
                IsActive         = true,
                IsFeatured       = true,
                IsNewArrival     = true,
                CategoryId       = bracelets.Id,
                Images = new List<ProductImage>
                {
                    new() { Url = "https://images.unsplash.com/photo-1611591437281-460bfbe1220a?w=600&q=80", IsPrimary = true, AltText = "Diamond Tennis Bracelet" }
                },
                StoreInventories = new List<StoreInventory>
                {
                    new() { StoreId = storeAbuja.Id,    QuantityOnHand = 4, QuantityReserved = 0, UpdatedAt = DateTime.UtcNow },
                    new() { StoreId = storeAllen.Id, QuantityOnHand = 2, QuantityReserved = 0, UpdatedAt = DateTime.UtcNow },
                    new() { StoreId = storeIkota.Id, QuantityOnHand = 2, QuantityReserved = 0, UpdatedAt = DateTime.UtcNow }
                }
            },
            new()
            {
                ExternalCode     = "SL-BRAC-002",
                Name             = "Gold Bangle Set",
                Slug             = "gold-bangle-set",
                ShortDescription = "A set of three stackable 18K yellow gold bangles in varying widths.",
                Description      = "Layer and express your personal style with this curated set of three 18K yellow gold bangles. Widths of 2mm, 3mm, and 4mm, each with a high-polish finish. Sized at 60mm inner diameter.",
                Price            = 165_000,
                Currency         = "NGN",
                Material         = "18K Yellow Gold",
                Metal            = "Yellow Gold",
                Sku              = "SL-BRAC-002",
                IsActive         = true,
                IsFeatured       = false,
                IsNewArrival     = false,
                CategoryId       = bracelets.Id,
                Images = new List<ProductImage>
                {
                    new() { Url = "https://images.unsplash.com/photo-1573408301185-9519f94815f3?w=600&q=80", IsPrimary = true, AltText = "Gold Bangle Set" }
                },
                StoreInventories = new List<StoreInventory>
                {
                    new() { StoreId = storeAbuja.Id,    QuantityOnHand = 8, QuantityReserved = 0, UpdatedAt = DateTime.UtcNow },
                    new() { StoreId = storeAllen.Id, QuantityOnHand = 6, QuantityReserved = 0, UpdatedAt = DateTime.UtcNow },
                    new() { StoreId = storeIkota.Id, QuantityOnHand = 4, QuantityReserved = 0, UpdatedAt = DateTime.UtcNow }
                }
            }
        };

        db.Products.AddRange(products);
        await db.SaveChangesAsync();
        logger.LogInformation("Seeded {Count} dummy products with per-store inventory.", products.Count);
    }
}
