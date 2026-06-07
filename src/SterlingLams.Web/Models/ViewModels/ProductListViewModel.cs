namespace SterlingLams.Web.Models.ViewModels;

public class ProductCardViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string PrimaryImageUrl { get; set; } = "/images/placeholder.jpg";
    public decimal Price { get; set; }
    public string Currency { get; set; } = "NGN";
    public string FormattedPrice => $"₦{Price:N0}";
    public bool IsAvailable { get; set; }
    public bool IsInWishlist { get; set; }
    public bool IsNewArrival { get; set; }
    public bool HasVariants { get; set; }
    public string CategoryName { get; set; } = string.Empty;
}

public class ProductListViewModel
{
    public List<ProductCardViewModel> Products { get; set; } = new();
    public ProductFilterViewModel Filters { get; set; } = new();

    public int TotalCount { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 24;
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);

    public string? ActiveCategory { get; set; }
    public string? PageTitle { get; set; }
}

public class ProductFilterViewModel
{
    public string? Search { get; set; }
    public string? Category { get; set; }
    public string? Metal { get; set; }
    public string? GemstoneType { get; set; }
    public string? Store { get; set; }
    public decimal? MinPrice { get; set; }
    public decimal? MaxPrice { get; set; }
    public string? SortBy { get; set; } = "newest";
    public bool? InStockOnly { get; set; }

    public List<string> AvailableMetals { get; set; } = new();
    public List<string> AvailableGemstones { get; set; } = new();
    public List<CategoryFilterOption> Categories { get; set; } = new();
}

public class CategoryFilterOption
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public int Count { get; set; }
}
