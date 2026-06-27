using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.Areas.Marketing.Controllers;

/// <summary>Customer signals the storefront already collects — abandoned carts + back-in-stock
/// requests — surfaced for the marketing team to act on. Read-only.</summary>
public class SignalsController : MarketingAreaController
{
    private readonly ApplicationDbContext _db;
    public SignalsController(ApplicationDbContext db) => _db = db;

    public async Task<IActionResult> Index(string tab = "carts")
    {
        ViewData["Title"] = "Customer Signals";
        ViewBag.Tab = tab == "backinstock" ? "backinstock" : "carts";
        ViewBag.CartCount = await _db.AbandonedCarts.CountAsync(c => c.RecoveredAt == null);
        ViewBag.BackInStockCount = await _db.BackInStockRequests.CountAsync(r => r.NotifiedAt == null);

        if ((string)ViewBag.Tab == "backinstock")
        {
            ViewBag.BackInStock = await _db.BackInStockRequests
                .Include(r => r.Product)
                .OrderByDescending(r => r.NotifiedAt == null).ThenByDescending(r => r.Id)
                .Take(200)
                .Select(r => new { r.Email, Product = r.Product.Name, r.NotifiedAt, r.CreatedAt })
                .ToListAsync();
        }
        else
        {
            ViewBag.Carts = await _db.AbandonedCarts
                .OrderByDescending(c => c.RecoveredAt == null).ThenByDescending(c => c.Id)
                .Take(200)
                .Select(c => new { c.Email, c.ItemCount, c.Subtotal, c.EmailedAt, c.RecoveredAt, c.CreatedAt })
                .ToListAsync();
        }
        return View();
    }
}
