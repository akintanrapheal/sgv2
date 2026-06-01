namespace SterlingLams.Web.Models.Domain;

public class ProductVariant
{
    public int Id { get; set; }
    public int OdooVariantId { get; set; }

    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
    public string? Size { get; set; }
    public string? Color { get; set; }
    public string? Finish { get; set; }

    public string? Sku { get; set; }
    public decimal? PriceAdjustment { get; set; }

    public bool IsActive { get; set; } = true;
}
