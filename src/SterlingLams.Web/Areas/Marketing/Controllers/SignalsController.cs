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
                .Select(c => new
                {
                    c.Email, c.ItemCount, c.Subtotal, c.EmailedAt, c.RecoveredAt, c.CreatedAt,
                    // Pull the shopper's name/phone from their account (when the cart email has one),
                    // so the board reads like Retainful's Live Carts and links to the contact profile.
                    User = _db.Users.Where(u => u.Email!.ToLower() == c.Email.ToLower())
                        .Select(u => new { u.Id, Name = u.FirstName + " " + u.LastName, u.PhoneNumber })
                        .FirstOrDefault()
                })
                .ToListAsync();

            // Recovery performance across all carts (not just the 200 shown): how many emailed
            // carts came back, and the cart value recovered.
            ViewBag.Emailed = await _db.AbandonedCarts.CountAsync(c => c.EmailedAt != null);
            ViewBag.Recovered = await _db.AbandonedCarts.CountAsync(c => c.RecoveredAt != null);
            ViewBag.RecoveredValue = await _db.AbandonedCarts
                .Where(c => c.RecoveredAt != null).SumAsync(c => (decimal?)c.Subtotal) ?? 0m;
        }
        return View();
    }
}
