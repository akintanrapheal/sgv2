using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services.Marketing;

namespace SterlingLams.Web.Areas.Marketing.Controllers;

public class DashboardController : MarketingAreaController
{
    private readonly ApplicationDbContext _db;
    private readonly IMarketingAttributionService _attribution;
    public DashboardController(ApplicationDbContext db, IMarketingAttributionService attribution)
    {
        _db = db;
        _attribution = attribution;
    }

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Marketing";
        ViewBag.Subscribers = await _db.NewsletterSubscribers.CountAsync();
        ViewBag.OpenCarts = await _db.AbandonedCarts.CountAsync(c => c.RecoveredAt == null);
        ViewBag.BackInStock = await _db.BackInStockRequests.CountAsync(r => r.NotifiedAt == null);
        ViewBag.Customers = await _db.Orders.Where(o => o.IsPaid).Select(o => o.UserId).Distinct().CountAsync();

        // Email-attributed revenue over the last 90 days (last-touch within the attribution window).
        var now = DateTime.UtcNow;
        ViewBag.Attribution = await _attribution.ComputeAsync(now.AddDays(-90), now);
        return View();
    }
}
