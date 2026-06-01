namespace SterlingLams.Web.Models.Domain;

public class Category
{
    public int Id { get; set; }
    public int? OdooCategoryId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }

    public int? ParentId { get; set; }
    public Category? Parent { get; set; }
    public ICollection<Category> Children { get; set; } = new List<Category>();
    public ICollection<Product> Products { get; set; } = new List<Product>();

    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}
