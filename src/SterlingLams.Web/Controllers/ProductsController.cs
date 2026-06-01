using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.ViewModels;
using SterlingLams.Web.Services.Inventory;

namespace SterlingLams.Web.Controllers;

public class ProductsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IInventoryService _inventory;
    private readonly ILogger<ProductsController> _logger;

    public ProductsController(ApplicationDbContext db, IInventoryService inventory, ILogger<ProductsController> logger)
    {
        _db = db;
        _inventory = inventory;
        _logger = logger;
    }

    // GET /products
    public async Task<IActionResult> Index(ProductFilterViewModel filters, int page = 1, int pageSize = 24)
    {
        var query = _db.Products
            .Include(p => p.Category)
            .Include(p => p.Images)
            .Include(p => p.StoreInventories)
            .Where(p => p.IsActive);

        if (!string.IsNullOrWhiteSpace(filters.Search))
            query = query.Where(p => EF.Functions.ILike(p.Name, $"%{filters.Search}%")
                || EF.Functions.ILike(p.Description ?? "", $"%{filters.Search}%"));

        if (!string.IsNullOrWhiteSpace(filters.Category))
            query = query.Where(p => p.Category.Slug == filters.Category);

        if (!string.IsNullOrWhiteSpace(filters.Metal))
            query = query.Where(p => p.Metal == filters.Metal);

        if (!string.IsNullOrWhiteSpace(filters.GemstoneType))
            query = query.Where(p => p.GemstoneType == filters.GemstoneType);

        if (filters.MinPrice.HasValue)
            query = query.Where(p => p.Price >= filters.MinPrice.Value);

        if (filters.MaxPrice.HasValue)
            query = query.Where(p => p.Price <= filters.MaxPrice.Value);

        if (filters.InStockOnly == true)
            query = query.Where(p => p.StoreInventories.Any(si => si.QuantityOnHand > 0));

        query = filters.SortBy switch
        {
            "price_asc" => query.OrderBy(p => p.Price),
            "price_desc" => query.OrderByDescending(p => p.Price),
            "name" => query.OrderBy(p => p.Name),
            _ => query.OrderByDescending(p => p.CreatedAt)
        };

        var totalCount = await query.CountAsync();
        var products = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var wishlistProductIds = User.Identity?.IsAuthenticated == true
            ? await _db.WishlistItems
                .Where(w => w.UserId == GetUserId())
                .Select(w => w.ProductId)
                .ToListAsync()
            : new List<int>();

        var vm = new ProductListViewModel
        {
            Products = products.Select(p => new ProductCardViewModel
            {
                Id = p.Id,
                Name = p.Name,
                Slug = p.Slug,
                Price = p.Price,
                Currency = p.Currency,
                PrimaryImageUrl = p.Images.FirstOrDefault(i => i.IsPrimary)?.Url
                    ?? p.Images.FirstOrDefault()?.Url
                    ?? "/images/placeholder.jpg",
                IsAvailable = p.StoreInventories.Any(si => si.QuantityOnHand > 0),
                IsInWishlist = wishlistProductIds.Contains(p.Id),
                IsNewArrival = p.IsNewArrival,
                CategoryName = p.Category.Name
            }).ToList(),
            Filters = filters,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            ActiveCategory = filters.Category
        };

        return View(vm);
    }

    // GET /products/{slug}
    [HttpGet("products/{slug}")]
    public async Task<IActionResult> Detail(string slug)
    {
        var product = await _db.Products
            .Include(p => p.Category)
            .Include(p => p.Images.OrderBy(i => i.SortOrder))
            .Include(p => p.Variants)
            .Include(p => p.StoreInventories).ThenInclude(si => si.Store)
            .Include(p => p.Tags)
            .FirstOrDefaultAsync(p => p.Slug == slug && p.IsActive);

        if (product == null) return NotFound();

        var isInWishlist = User.Identity?.IsAuthenticated == true
            && await _db.WishlistItems.AnyAsync(w => w.UserId == GetUserId() && w.ProductId == product.Id);

        var relatedProducts = await _db.Products
            .Include(p => p.Images)
            .Include(p => p.StoreInventories)
            .Where(p => p.CategoryId == product.CategoryId && p.Id != product.Id && p.IsActive)
            .OrderBy(_ => EF.Functions.Random())
            .Take(4)
            .ToListAsync();

        var vm = new ProductDetailViewModel
        {
            Id = product.Id,
            Name = product.Name,
            Slug = product.Slug,
            Description = product.Description,
            ShortDescription = product.ShortDescription,
            Price = product.Price,
            Currency = product.Currency,
            Material = product.Material,
            Metal = product.Metal,
            GemstoneType = product.GemstoneType,
            Carat = product.Carat,
            Weight = product.Weight,
            CategoryName = product.Category.Name,
            CategorySlug = product.Category.Slug,
            ImageUrls = product.Images.Select(i => i.Url).ToList(),
            StoreStock = product.StoreInventories.Select(si => new StoreStockViewModel
            {
                StoreName = si.Store.Name,
                StoreSlug = si.Store.Slug,
                Quantity = Math.Max(0, si.QuantityOnHand - si.QuantityReserved)
            }).ToList(),
            Variants = product.Variants.Select(v => new ProductVariantOptionViewModel
            {
                Id = v.Id,
                Name = v.Name,
                Size = v.Size,
                Color = v.Color,
                PriceAdjustment = v.PriceAdjustment
            }).ToList(),
            Tags = product.Tags.Select(t => t.Name).ToList(),
            IsInWishlist = isInWishlist,
            RelatedProducts = relatedProducts.Select(p => new ProductCardViewModel
            {
                Id = p.Id,
                Name = p.Name,
                Slug = p.Slug,
                Price = p.Price,
                PrimaryImageUrl = p.Images.FirstOrDefault()?.Url ?? "/images/placeholder.jpg",
                IsAvailable = p.StoreInventories.Any(si => si.QuantityOnHand > 0)
            }).ToList()
        };

        return View(vm);
    }

    // GET /api/products/{id}/inventory (AJAX)
    [HttpGet("api/products/{id:int}/inventory")]
    public async Task<IActionResult> GetInventory(int id)
    {
        var product = await _db.Products.FindAsync(id);
        if (product == null) return NotFound();

        var inventory = await _inventory.GetStoreInventoryForProductAsync(product.OdooProductId);
        return Json(inventory);
    }

    private string GetUserId() => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
}
