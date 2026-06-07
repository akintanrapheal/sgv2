using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.ViewModels;

namespace SterlingLams.Web.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly ApplicationDbContext _db;

    public HomeController(ILogger<HomeController> logger, ApplicationDbContext db)
    {
        _logger = logger;
        _db = db;
    }

    public async Task<IActionResult> Index()
    {
        // Featured products from DB (IsFeatured = true, active, limit 4)
        var featured = await _db.Products
            .Include(p => p.Images)
            .Include(p => p.StoreInventories)
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
            Currency = p.Currency,
            PrimaryImageUrl = p.Images.FirstOrDefault(i => i.IsPrimary)?.Url
                ?? p.Images.FirstOrDefault()?.Url
                ?? "/images/placeholder.jpg",
            IsAvailable = p.StoreInventories.Any(si => si.QuantityOnHand > 0)
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

        return View();
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
        return View();
    }
}
