namespace SterlingLams.Web.Models.ViewModels;

public class StoreStockViewModel
{
    public string StoreName { get; set; } = string.Empty;
    public string StoreSlug { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public bool IsAvailable => Quantity > 0;
}

public class ProductDetailViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Sku { get; set; }
    public string? Description { get; set; }
    public string? ShortDescription { get; set; }
    public decimal Price { get; set; }
    public decimal? SalePrice { get; set; }
    public string Currency { get; set; } = "NGN";
    public bool IsOnSale => SalePrice is decimal s && s > 0m && s < Price;
    public decimal EffectivePrice => IsOnSale ? SalePrice!.Value : Price;
    /// <summary>Sale price when on sale, otherwise the regular price.</summary>
    public string FormattedPrice => $"₦{EffectivePrice:N0}";
    /// <summary>Regular price — render struck-through when <see cref="IsOnSale"/>.</summary>
    public string FormattedRegularPrice => $"₦{Price:N0}";
    /// <summary>Whole-number % off (e.g. 21) when on sale, else 0 — drives the discount badge.</summary>
    public int DiscountPercent => IsOnSale && Price > 0m
        ? (int)Math.Round((Price - SalePrice!.Value) / Price * 100m)
        : 0;

    public string? Material { get; set; }
    public string? Metal { get; set; }
    public string? GemstoneType { get; set; }
    public string? Carat { get; set; }
    public string? Weight { get; set; }

    public string CategoryName { get; set; } = string.Empty;
    public string CategorySlug { get; set; } = string.Empty;

    public List<string> ImageUrls { get; set; } = new();
    public string PrimaryImageUrl => ImageUrls.FirstOrDefault() ?? "/images/placeholder.jpg";

    public List<StoreStockViewModel> StoreStock { get; set; } = new();
    public bool IsAvailableAnywhere => StoreStock.Any(s => s.IsAvailable);
    public int TotalStock => StoreStock.Sum(s => s.Quantity);

    public List<ProductVariantOptionViewModel> Variants { get; set; } = new();
    public List<string> Tags { get; set; } = new();

    public bool IsInWishlist { get; set; }
    /// <summary>Admin-configurable copy (store.out_of_stock_msg) shown when the item is unavailable.</summary>
    public string OutOfStockMessage { get; set; } = "This item is currently out of stock. Check back soon.";
    public List<ProductCardViewModel> RelatedProducts { get; set; } = new();
    public List<ProductCardViewModel> FrequentlyBoughtTogether { get; set; } = new();

    // ── Reviews ──────────────────────────────────────────────────────────────
    public bool ReviewsEnabled { get; set; } = true;
    public double AverageRating { get; set; }
    public int ReviewCount { get; set; }
    public List<ProductReviewViewModel> Reviews { get; set; } = new();
    /// <summary>The visitor is signed in (and so may submit a review).</summary>
    public bool CanReview { get; set; }
    /// <summary>The signed-in visitor has already reviewed this product.</summary>
    public bool HasReviewed { get; set; }
    /// <summary>Counts per star (1..5) for the rating breakdown bars.</summary>
    public Dictionary<int, int> RatingBreakdown { get; set; } = new();
}

public class ProductReviewViewModel
{
    public string AuthorName { get; set; } = string.Empty;
    public int Rating { get; set; }
    public string? Title { get; set; }
    public string Body { get; set; } = string.Empty;
    public bool IsVerifiedBuyer { get; set; }
    public string? AdminReply { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class AttributeLabelViewModel
{
    public string AttributeName { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string? ColorHex { get; set; }
}

public class ProductVariantOptionViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal? PriceAdjustment { get; set; }
    public string? ImageUrl { get; set; }   // optional per-variant image (swaps the main image when selected)
    public int Available { get; set; }   // combined available across active branches (per-variant, with pool fallback)
    public List<StoreStockViewModel> StoreStock { get; set; } = new();   // per-branch availability for this variant
    public List<AttributeLabelViewModel> AttributeLabels { get; set; } = new();
}
