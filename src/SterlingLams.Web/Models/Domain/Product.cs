namespace SterlingLams.Web.Models.Domain;

public class Product
{
    public int Id { get; set; }

    /// <summary>Odoo product template ID (source of truth)</summary>
    public int OdooProductId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ShortDescription { get; set; }

    public decimal Price { get; set; }
    public string Currency { get; set; } = "NGN";

    public string? Sku { get; set; }
    public string? Barcode { get; set; }

    public string? Material { get; set; }
    public string? Metal { get; set; }
    public string? GemstoneType { get; set; }
    public string? Carat { get; set; }
    public string? Weight { get; set; }

    public bool IsActive { get; set; } = true;
    public bool IsFeatured { get; set; }
    public bool IsNewArrival { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public int CategoryId { get; set; }
    public Category Category { get; set; } = null!;

    public ICollection<ProductImage> Images { get; set; } = new List<ProductImage>();
    public ICollection<ProductVariant> Variants { get; set; } = new List<ProductVariant>();
    public ICollection<StoreInventory> StoreInventories { get; set; } = new List<StoreInventory>();
    public ICollection<WishlistItem> WishlistItems { get; set; } = new List<WishlistItem>();
    public ICollection<ProductTag> Tags { get; set; } = new List<ProductTag>();

    public int TotalStock => StoreInventories.Sum(si => si.QuantityOnHand);
    public bool IsAvailable => TotalStock > 0;
}
