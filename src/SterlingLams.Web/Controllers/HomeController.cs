using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.ViewModels;

namespace SterlingLams.Web.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly ApplicationDbContext _db;
    private readonly SterlingLams.Web.Services.IMerchandisingService _merch;

    public HomeController(ILogger<HomeController> logger, ApplicationDbContext db,
        SterlingLams.Web.Services.IMerchandisingService merch)
    {
        _logger = logger;
        _db = db;
        _merch = merch;
    }

    public async Task<IActionResult> Index()
    {
        // Featured products from DB (IsFeatured = true, active, limit 4)
        var featured = await _db.Products
            .Include(p => p.Images)
            .Include(p => p.StoreInventories)
            .Include(p => p.Variants)
            .Where(p => p.IsActive && p.IsFeatured)
            .OrderByDescending(p => p.CreatedAt)
            .Take(4)
            .ToListAsync();

        ViewBag.FeaturedProducts = featured.Select(p => new ProductCardViewModel
        {
            Id = p.Id,
            Name = p.Name,
            Slug = p.Slug,
            Price = p.Price,
            SalePrice = p.SalePrice,
            Currency = p.Currency,
            PrimaryImageUrl = p.Images.FirstOrDefault(i => i.IsPrimary)?.Url
                ?? p.Images.FirstOrDefault()?.Url
                ?? "/images/placeholder.jpg",
            IsAvailable = p.StoreInventories.Any(si => si.QuantityOnHand > 0),
            HasVariants = p.Variants.Any(v => v.IsActive)
        }).ToList();

        // "Shop by Category" — active top-level categories. Prefer those with an
        // image (admin-curated); fall back to the first few so the section is never empty.
        var topCategories = await _db.Categories
            .Where(c => c.IsActive && c.ParentId == null)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .ToListAsync();

        var withImages = topCategories.Where(c => !string.IsNullOrWhiteSpace(c.ImageUrl)).ToList();
        ViewBag.ShopCategories = withImages.Count > 0 ? withImages : topCategories.Take(5).ToList();

        // ─── Merchandising rows ──────────────────────────────────────────────
        ViewBag.BestSellers = await _merch.BestSellersAsync(4);                 // all-time
        ViewBag.Trending = await _merch.BestSellersAsync(4, sinceDays: 30);     // last 30 days
        ViewBag.RecentlyViewed = await _merch.ByIdsAsync(
            SterlingLams.Web.Infrastructure.RecentlyViewed.Get(Request));

        return View();
    }

    [HttpPost]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth")]
    public async Task<IActionResult> Subscribe(string email)
    {
        email = (email ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@') || email.Length is < 5 or > 200)
            return Json(new { success = false, message = "Please enter a valid email address." });

        if (!await _db.NewsletterSubscribers.AnyAsync(s => s.Email == email))
        {
            _db.NewsletterSubscribers.Add(new Models.Domain.NewsletterSubscriber { Email = email, CreatedAt = DateTime.UtcNow });
            await _db.SaveChangesAsync();
        }
        return Json(new { success = true, message = "Thank you for subscribing!" });
    }

    public IActionResult Collections()
    {
        return View();
    }

    public IActionResult About()
    {
        return View();
    }

    public IActionResult Contact()
    {
        return View();
    }

    public IActionResult Privacy()
    {
        return View();
    }

    public IActionResult Terms()
    {
        return View();
    }

    public IActionResult PaymentReturns()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        // Staff areas get a back-office error page (no storefront chrome) instead of being dumped
        // on the customer-facing "Something went wrong" page.
        var path = HttpContext.Features
            .Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>()?.Path ?? "";
        if (IsStaffPath(path)) return View("StaffError");
        return View();
    }

    private static bool IsStaffPath(string path) =>
        path.StartsWith("/Admin", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/Inventory", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/Till", StringComparison.OrdinalIgnoreCase);
}
