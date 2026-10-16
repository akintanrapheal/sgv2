using System.Collections.Generic;
using System.Threading.Tasks;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>Order emails must show the chosen VARIANT's own picture when it has one (so a "Gold" line shows
/// the gold photo, not the product's default/silver image, which otherwise makes a packer grab the wrong
/// colour). Falls back to the product's primary image when the variant has no image of its own.</summary>
public class OrderEmailImagesTests
{
    [Fact]
    public async Task Variant_image_wins_else_product_primary()
    {
        using var t = new TestDb();
        var p = t.SeedProduct(1000m);
        t.Db.ProductImages.Add(new ProductImage { ProductId = p.Id, Url = "https://img/product-primary.jpg", IsPrimary = true });
        var gold = new ProductVariant { ProductId = p.Id, Name = "Gold", IsActive = true, ImageUrl = "https://img/gold.jpg" };
        var silver = new ProductVariant { ProductId = p.Id, Name = "Silver", IsActive = true, ImageUrl = null };
        t.Db.ProductVariants.AddRange(gold, silver);
        await t.Db.SaveChangesAsync();

        var items = new List<OrderItem>
        {
            new() { ProductId = p.Id, ProductVariantId = gold.Id,   ProductName = p.Name, VariantName = "Gold",   Quantity = 1 },
            new() { ProductId = p.Id, ProductVariantId = silver.Id, ProductName = p.Name, VariantName = "Silver", Quantity = 1 },
            new() { ProductId = p.Id, ProductVariantId = null,       ProductName = p.Name,                        Quantity = 1 },
        };

        var img = await OrderEmailImages.ResolverAsync(t.Db, items, null);

        Assert.Equal("https://img/gold.jpg", img(items[0]));             // variant with its own image → that image
        Assert.Equal("https://img/product-primary.jpg", img(items[1]));  // variant without image → product primary
        Assert.Equal("https://img/product-primary.jpg", img(items[2]));  // no variant → product primary
    }

    [Fact]
    public async Task Relative_url_is_made_absolute_with_base()
    {
        using var t = new TestDb();
        var p = t.SeedProduct(1000m);
        t.Db.ProductImages.Add(new ProductImage { ProductId = p.Id, Url = "images/p.jpg", IsPrimary = true });
        await t.Db.SaveChangesAsync();

        var items = new List<OrderItem> { new() { ProductId = p.Id, ProductName = p.Name, Quantity = 1 } };
        var img = await OrderEmailImages.ResolverAsync(t.Db, items, "https://sterlinglams.com/");
        Assert.Equal("https://sterlinglams.com/images/p.jpg", img(items[0]));
    }
}
