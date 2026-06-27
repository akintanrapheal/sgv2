using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.Areas.Marketing.Controllers;

public class DashboardController : MarketingAreaController
{
    private readonly ApplicationDbContext _db;
    public DashboardController(ApplicationDbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Marketing";
        ViewBag.Subscribers = await _db.NewsletterSubscribers.CountAsync();
        ViewBag.OpenCarts = await _db.AbandonedCarts.CountAsync(c => c.RecoveredAt == null);
        ViewBag.BackInStock = await _db.BackInStockRequests.CountAsync(r => r.NotifiedAt == null);
        ViewBag.Customers = await _db.Orders.Where(o => o.IsPaid).Select(o => o.UserId).Distinct().CountAsync();
        return View();
    }
}
