using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.ViewModels;

namespace SterlingLams.Web.Controllers;

public class ProductsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<ProductsController> _logger;

    public ProductsController(ApplicationDbContext db, ILogger<ProductsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    // GET /products
    public async Task<IActionResult> Index(ProductFilterViewModel filters, int page = 1, int pageSize = 24)
    {
        var query = _db.Products
            .Include(p => p.Category)
            .Include(p => p.Images)
            .Include(p => p.StoreInventories)
            .Include(p => p.Variants)
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
                HasVariants = p.Variants.Any(v => v.IsActive),
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
            .Include(p => p.Variants).ThenInclude(v => v.AttributeValues).ThenInclude(av => av.Attribute)
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
            Sku = product.Sku,
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
            Variants = product.Variants.Where(v => v.IsActive).Select(v => new ProductVariantOptionViewModel
            {
                Id = v.Id,
                Name = v.Name,
                PriceAdjustment = v.PriceAdjustment,
                AttributeLabels = v.AttributeValues
                    .OrderBy(av => av.Attribute.SortOrder)
                    .Select(av => new AttributeLabelViewModel
                    {
                        AttributeName = av.Attribute.Name,
                        Value         = av.Value,
                        ColorHex      = av.ColorHex,
                    }).ToList()
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

    // GET /api/search?q=diamond  (live search suggestions — separate route to avoid slug conflict)
    [HttpGet("/api/search")]
    public async Task<IActionResult> SearchSuggestions(string q)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            return Json(Array.Empty<object>());

        var results = await _db.Products
            .Where(p => p.IsActive && (
                EF.Functions.ILike(p.Name, $"%{q}%") ||
                EF.Functions.ILike(p.ShortDescription ?? "", $"%{q}%")))
            .OrderBy(p => p.Name)
            .Take(6)
            .Select(p => new
            {
                p.Name,
                p.Slug,
                p.Price
            })
            .ToListAsync();

        return Json(results);
    }

    // POST /Products/NotifyRestock  (back-in-stock email capture)
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult NotifyRestock(int productId, string email)
    {
        if (!string.IsNullOrWhiteSpace(email))
            _logger.LogInformation("Restock notification requested: product {ProductId} for {Email}", productId, email);

        return Json(new { success = true });
    }

    private string GetUserId() => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
}
