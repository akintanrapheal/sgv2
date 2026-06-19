namespace SterlingLams.Web.Models.Domain;

public class ProductVariant
{
    public int Id { get; set; }

    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;

    // Auto-generated from attribute values e.g. "Gold / 18\" / A"
    public string Name { get; set; } = string.Empty;
    public string? Sku { get; set; }
    public string? Barcode { get; set; }
    public decimal? PriceAdjustment { get; set; }
    public int StockQuantity { get; set; }
    public bool IsActive { get; set; } = true;

    // Optional image shown on the storefront when this variant (e.g. "Gold") is selected.
    public string? ImageUrl { get; set; }

    // Which attribute values make up this variant (many-to-many)
    public ICollection<ProductAttributeValue> AttributeValues { get; set; } = new List<ProductAttributeValue>();
}
