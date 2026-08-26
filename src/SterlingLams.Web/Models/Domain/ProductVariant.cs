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
    // Absolute selling price for this variant, set manually. Null = fall back to the product's
    // (sale-aware) price. There is no auto-calculation from the base price any more.
    public decimal? Price { get; set; }

    // Optional absolute promotional/sale price for THIS variant, set manually. When set and below the
    // variant's effective regular price, this is the price charged (within the product's sale window),
    // and the storefront strikes through the regular price. Null = this variant is not independently on
    // sale (a variant with its own Price only goes on sale when this is set; a variant with a null Price
    // inherits the product's sale). Effective pricing lives in Domain.VariantPricing — do not re-derive.
    public decimal? SalePrice { get; set; }
    public int StockQuantity { get; set; }
    public bool IsActive { get; set; } = true;

    // Optional image shown on the storefront when this variant (e.g. "Gold") is selected.
    public string? ImageUrl { get; set; }

    // Which attribute values make up this variant (many-to-many)
    public ICollection<ProductAttributeValue> AttributeValues { get; set; } = new List<ProductAttributeValue>();
}
