namespace SterlingLams.Web.Models.Domain;

public enum DiscountType
{
    Percentage,
    FixedAmount,
    FreeShipping
}

public enum DiscountScope
{
    EntireOrder,
    Categories,
    Products
}

public class DiscountCode
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }

    public DiscountType Type { get; set; } = DiscountType.Percentage;
    public decimal Value { get; set; }

    /// <summary>What the discount applies to: whole order, certain categories, or certain products.</summary>
    public DiscountScope Scope { get; set; } = DiscountScope.EntireOrder;

    /// <summary>If true, applies automatically with no code needed (a sale/promotion).</summary>
    public bool IsAutomatic { get; set; }

    public decimal? MinimumOrderAmount { get; set; }
    public int? MinimumQuantity { get; set; }
    public int? MaxUses { get; set; }
    public int? MaxUsesPerCustomer { get; set; }
    public bool FirstOrderOnly { get; set; }
    public int UsedCount { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime? StartsAt { get; set; }
    public DateTime? ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Scope targets (used when Scope is Categories / Products)
    public ICollection<DiscountCategory> Categories { get; set; } = new List<DiscountCategory>();
    public ICollection<DiscountProduct> Products { get; set; } = new List<DiscountProduct>();

    // ── Status helpers (for admin badges) ───────────────────────────────────
    public bool IsScheduled => StartsAt.HasValue && DateTime.UtcNow < StartsAt.Value;
    public bool IsExpired   => ExpiresAt.HasValue && DateTime.UtcNow > ExpiresAt.Value;
    public bool IsUsedUp    => MaxUses.HasValue && UsedCount >= MaxUses.Value;
    public bool IsLive      => IsActive && !IsScheduled && !IsExpired && !IsUsedUp;
}

public class DiscountCategory
{
    public int Id { get; set; }
    public int DiscountCodeId { get; set; }
    public DiscountCode DiscountCode { get; set; } = null!;
    public int CategoryId { get; set; }
    public Category Category { get; set; } = null!;
}

public class DiscountProduct
{
    public int Id { get; set; }
    public int DiscountCodeId { get; set; }
    public DiscountCode DiscountCode { get; set; } = null!;
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;
}
