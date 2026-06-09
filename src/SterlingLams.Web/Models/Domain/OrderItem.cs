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
    public decimal LineTotal => (Quantity * UnitPrice) - DiscountAmount;
}
