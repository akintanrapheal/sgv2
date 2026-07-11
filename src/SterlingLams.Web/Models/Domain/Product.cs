namespace SterlingLams.Web.Models.Domain;

public class Product
{
    public int Id { get; set; }

    /// <summary>Stable external/import code (e.g. the WooCommerce SKU-derived key). Unique upsert key for imports.</summary>
    public string ExternalCode { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ShortDescription { get; set; }

    public decimal Price { get; set; }

    /// <summary>Optional unit cost price (what we pay for the item). Used for gross-profit / margin
    /// reporting. Null = cost not recorded yet, so the item is excluded from margin figures.</summary>
    public decimal? CostPrice { get; set; }

    /// <summary>Optional promotional/sale price. When set and below <see cref="Price"/>, this is the
    /// price actually charged, and the storefront shows the regular Price struck-through next to it.
    /// Null (or not below Price) = not on sale.</summary>
    public decimal? SalePrice { get; set; }

    /// <summary>Optional sale window (UTC). When set, the sale price only applies between these
    /// times; null = open-ended on that side (so both null = the sale is live as soon as it's set,
    /// the original behaviour). Evaluated at read time — no background job, no clock-skew lag.</summary>
    public DateTime? SaleStartsAt { get; set; }
    public DateTime? SaleEndsAt { get; set; }

    /// <summary>True when a valid sale price is in effect AND within its scheduled window (if any).</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool IsOnSale => SalePrice is decimal s && s > 0m && s < Price
        && (SaleStartsAt == null || SaleStartsAt <= DateTime.UtcNow)
        && (SaleEndsAt == null || SaleEndsAt >= DateTime.UtcNow);

    /// <summary>The price actually charged: the sale price when on sale, otherwise the regular price.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public decimal EffectivePrice => IsOnSale ? SalePrice!.Value : Price;

    public string Currency { get; set; } = "NGN";

    public string? Sku { get; set; }
    public string? Barcode { get; set; }

    public string? Material { get; set; }
    public string? Metal { get; set; }
    public string? GemstoneType { get; set; }
    public string? Carat { get; set; }
    public string? Weight { get; set; }

    /// <summary>"simple" (no options) or "variable" (has variants/options).</summary>
    public string ProductType { get; set; } = "simple";

    public bool IsActive { get; set; } = true;
    /// <summary>Archived = retired from the working product list (Inventory "Archived" tab). Distinct
    /// from IsActive (storefront visibility); an archived product is hidden from the Current list.</summary>
    public bool IsArchived { get; set; }
    /// <summary>Whether stock is tracked for this product (Inventory "Track Stock"). Untracked items
    /// (services, made-to-order) skip stock checks/levels.</summary>
    public bool TrackStock { get; set; } = true;
    /// <summary>Optional POS tile/button colour for this product (Inventory "Button colour").</summary>
    public string? PosButtonColour { get; set; }
    /// <summary>Hidden from the POS/till only (staff "Mark unavailable") — still listed on the
    /// storefront. Independent of <see cref="IsActive"/>.</summary>
    public bool HiddenFromPos { get; set; }
    /// <summary>Marks the single hidden "Custom item" product the POS uses for one-off / un-barcoded
    /// sales. Such lines carry a per-sale name and price and skip stock tracking. Lazily created.</summary>
    public bool IsCustomItem { get; set; }
    public bool IsFeatured { get; set; }
    public int LowStockThreshold { get; set; } = 3;
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
