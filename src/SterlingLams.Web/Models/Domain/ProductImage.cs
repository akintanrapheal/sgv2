namespace SterlingLams.Web.Models.Domain;

public class ProductImage
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public string Url { get; set; } = string.Empty;
    public string? AltText { get; set; }
    public int SortOrder { get; set; }
    public bool IsPrimary { get; set; }

    /// <summary>The "secondary" image revealed on card hover (Tiffany-style swap). At most one per
    /// product, and never the same image as <see cref="IsPrimary"/>.</summary>
    public bool IsHover { get; set; }
}
