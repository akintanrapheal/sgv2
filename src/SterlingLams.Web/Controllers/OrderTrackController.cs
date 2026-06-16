using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.ViewModels;

namespace SterlingLams.Web.Controllers;

/// <summary>
/// Public "track your order" lookup so guests (and signed-out customers) can check an order's
/// status after leaving the confirmation page — gated by order number AND the email on the order,
/// rate-limited, with a single generic "not found" message so it can't be used to probe.
/// </summary>
[AllowAnonymous]
public class OrderTrackController : Controller
{
    private readonly ApplicationDbContext _db;
    public OrderTrackController(ApplicationDbContext db) => _db = db;

    [HttpGet("/track")]
    public IActionResult Index() => View(new TrackOrderViewModel());

    [HttpPost("/track")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Index(TrackOrderViewModel vm)
    {
        if (!ModelState.IsValid) return View(vm);
        vm.Searched = true;

        var orderNumber = vm.OrderNumber.Trim();
        var email = vm.Email.Trim();

        var order = await _db.Orders
            .Include(o => o.Items)
            .Include(o => o.User)
            .Include(o => o.PickupStore)
            .Include(o => o.DeliveryAddress)
            .FirstOrDefaultAsync(o => o.OrderNumber == orderNumber);

        // Require both the number and a matching email; never reveal which part was wrong.
        if (order == null || !string.Equals(order.User.Email, email, StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError("", "We couldn't find an order with that number and email. Please check both and try again.");
            return View(vm);
        }

        vm.Order = order;
        return View(vm);
    }
}
