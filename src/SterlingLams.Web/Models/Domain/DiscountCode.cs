namespace SterlingLams.Web.Models.Domain;

public enum DiscountType
{
    Percentage,
    FixedAmount
}

public class DiscountCode
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }

    public DiscountType Type { get; set; } = DiscountType.Percentage;
    public decimal Value { get; set; }

    public decimal? MinimumOrderAmount { get; set; }
    public int? MaxUses { get; set; }
    public int UsedCount { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime? StartsAt { get; set; }
    public DateTime? ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public bool IsValid(decimal orderAmount)
    {
        if (!IsActive) return false;
        if (MaxUses.HasValue && UsedCount >= MaxUses.Value) return false;
        if (StartsAt.HasValue && DateTime.UtcNow < StartsAt.Value) return false;
        if (ExpiresAt.HasValue && DateTime.UtcNow > ExpiresAt.Value) return false;
        if (MinimumOrderAmount.HasValue && orderAmount < MinimumOrderAmount.Value) return false;
        return true;
    }

    public decimal CalculateDiscount(decimal orderAmount)
    {
        if (Type == DiscountType.Percentage)
            return Math.Round(orderAmount * Value / 100, 2);
        return Math.Min(Value, orderAmount);
    }
}
