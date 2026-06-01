namespace SterlingLams.Web.Models.Domain;

public class ProductTag
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public ICollection<Product> Products { get; set; } = new List<Product>();
}
