namespace SterlingLams.Web.Models.Domain;

public class OrderItem
{
    public int Id { get; set; }

    public int OrderId { get; set; }
    public Order Order { get; set; } = null!;

    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public int? ProductVariantId { get; set; }
    public ProductVariant? ProductVariant { get; set; }

    public string ProductName { get; set; } = string.Empty;
    public string? VariantName { get; set; }

    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountAmount { get; set; }
    public string? DiscountReason { get; set; }
    public string? DiscountType { get; set; }
    public string? ItemNote { get; set; }
    public string? ProductSku { get; set; }

    /// <summary>The branch whose stock actually supplied this line (set at fulfilment). For a split
    /// online order each line is credited to the branch that packed it — so finance by-store + EOD
    /// attribute per item, not the whole order to the pickup branch. Null = legacy/unset: callers fall
    /// back to the order's PickupStoreId/FulfillingStoreId.</summary>
    public int? FulfilledFromStoreId { get; set; }

    public decimal LineTotal => (Quantity * UnitPrice) - DiscountAmount;
}
