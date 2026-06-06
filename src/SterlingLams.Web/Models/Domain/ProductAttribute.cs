namespace SterlingLams.Web.Models.Domain;

public class ProductAttribute
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;   // e.g. "Colour"
    public string Slug { get; set; } = string.Empty;   // e.g. "colour"
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    public ICollection<ProductAttributeValue> Values { get; set; } = new List<ProductAttributeValue>();
}

public class ProductAttributeValue
{
    public int Id { get; set; }
    public int AttributeId { get; set; }
    public ProductAttribute Attribute { get; set; } = null!;

    public string Value { get; set; } = string.Empty;  // e.g. "Gold"
    public string? ColorHex { get; set; }              // optional swatch colour
    public int SortOrder { get; set; }

    public ICollection<ProductVariant> Variants { get; set; } = new List<ProductVariant>();
}
