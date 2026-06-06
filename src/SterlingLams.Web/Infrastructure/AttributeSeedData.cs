using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Infrastructure;

public static class AttributeSeedData
{
    public static async Task SeedAsync(ApplicationDbContext db, ILogger logger)
    {
        await SeedColourAsync(db, logger);
        await SeedAlphabetAsync(db, logger);
        await SeedSizeAsync(db, logger);
        await SeedLengthAsync(db, logger);
        await SeedComboAsync(db, logger);
        await db.SaveChangesAsync();
    }

    private static async Task SeedColourAsync(ApplicationDbContext db, ILogger logger)
    {
        if (await db.ProductAttributes.AnyAsync(a => a.Slug == "colour")) return;

        var attr = new ProductAttribute
        {
            Name = "Colour", Slug = "colour", IsActive = true, SortOrder = 1
        };

        var values = new[]
        {
            // Single colours
            ("Gold",             "#FFD700"),
            ("Silver",           "#C0C0C0"),
            ("Rose Gold",        "#B76E79"),
            ("3-Tone",           (string?)null),
            ("Blue",             "#4169E1"),
            ("Green",            "#228B22"),
            ("Yellow",           "#FFE135"),
            ("Red",              "#CC0000"),
            ("Black",            "#1C1C1C"),
            ("White",            "#F5F5F5"),
            ("Grey",             "#808080"),
            ("Teal",             "#008080"),
            ("Amber",            "#FFBF00"),
            ("Blue Mix",         "#5B9BD5"),
            ("Multi",            (string?)null),
            // Two-tone combinations
            ("Gold/Silver",      (string?)null),
            ("Gold/Rose Gold",   (string?)null),
            ("Gold/White",       (string?)null),
            ("Gold/Baby Pink",   (string?)null),
            ("Gold/Orange",      (string?)null),
            ("Rose Gold/Silver", (string?)null),
            ("Green/Gold",       (string?)null),
            ("Green/Silver",     (string?)null),
            ("Teal/Silver",      (string?)null),
            ("Silver/Blue",      (string?)null),
            ("Silver/Red",       (string?)null),
            ("Silver/White",     (string?)null),
            ("Silver/Yellow",    (string?)null),
            ("Red/Gold",         (string?)null),
            ("Multi/Silver",     (string?)null),
        };

        int sort = 0;
        foreach (var (value, hex) in values)
            attr.Values.Add(new ProductAttributeValue { Value = value, ColorHex = hex, SortOrder = ++sort });

        db.ProductAttributes.Add(attr);
        logger.LogInformation("Seeded Colour attribute with {Count} values.", values.Length);
    }

    private static async Task SeedAlphabetAsync(ApplicationDbContext db, ILogger logger)
    {
        if (await db.ProductAttributes.AnyAsync(a => a.Slug == "alphabet")) return;

        var attr = new ProductAttribute
        {
            Name = "Alphabet", Slug = "alphabet", IsActive = true, SortOrder = 2
        };

        int sort = 0;
        foreach (var letter in "ABCDEFGHIJKLMNOPQRSTUVWXYZ")
            attr.Values.Add(new ProductAttributeValue { Value = letter.ToString(), SortOrder = ++sort });

        db.ProductAttributes.Add(attr);
        logger.LogInformation("Seeded Alphabet attribute with 26 values.");
    }

    private static async Task SeedSizeAsync(ApplicationDbContext db, ILogger logger)
    {
        if (await db.ProductAttributes.AnyAsync(a => a.Slug == "size")) return;

        var attr = new ProductAttribute
        {
            Name = "Size", Slug = "size", IsActive = true, SortOrder = 3
        };

        int sort = 0;
        foreach (var s in new[] { "XS", "S", "M", "L", "XL", "One Size" })
            attr.Values.Add(new ProductAttributeValue { Value = s, SortOrder = ++sort });

        db.ProductAttributes.Add(attr);
        logger.LogInformation("Seeded Size attribute.");
    }

    private static async Task SeedLengthAsync(ApplicationDbContext db, ILogger logger)
    {
        if (await db.ProductAttributes.AnyAsync(a => a.Slug == "length")) return;

        var attr = new ProductAttribute
        {
            Name = "Length", Slug = "length", IsActive = true, SortOrder = 4
        };

        int sort = 0;
        foreach (var l in new[] { "14 inch", "16 inch", "18 inch", "20 inch", "22 inch", "24 inch" })
            attr.Values.Add(new ProductAttributeValue { Value = l, SortOrder = ++sort });

        db.ProductAttributes.Add(attr);
        logger.LogInformation("Seeded Length attribute.");
    }

    private static async Task SeedComboAsync(ApplicationDbContext db, ILogger logger)
    {
        if (await db.ProductAttributes.AnyAsync(a => a.Slug == "combo")) return;

        var attr = new ProductAttribute
        {
            Name = "Combo", Slug = "combo", IsActive = true, SortOrder = 5
        };

        int sort = 0;
        foreach (var c in new[]
        {
            "Ring Only", "Necklace Only", "Earrings Only",
            "Ring + Necklace", "Ring + Earrings",
            "Necklace + Earrings", "Full Set"
        })
            attr.Values.Add(new ProductAttributeValue { Value = c, SortOrder = ++sort });

        db.ProductAttributes.Add(attr);
        logger.LogInformation("Seeded Combo attribute.");
    }
}
