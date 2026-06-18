using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

// Moniebook-style CRM inside the Inventory System: customer directory (with order/spend rollups)
// and the discount-code list. Read views over the existing Users / DiscountCodes.
public class CrmController : InventoryAreaController
{
    private readonly ApplicationDbContext _db;
    private const int PageSize = 30;
    public CrmController(ApplicationDbContext db) => _db = db;

    public async Task<IActionResult> Customers(string q = "", int page = 1)
    {
        ViewData["Title"] = "Customers";
        var query = _db.Users.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(u => EF.Functions.ILike(u.FirstName + " " + u.LastName, $"%{q}%")
                                  || EF.Functions.ILike(u.Email!, $"%{q}%")
                                  || EF.Functions.ILike(u.PhoneNumber ?? "", $"%{q}%"));
        // Only people who have actually transacted — keeps staff-only accounts out of the directory.
        query = query.Where(u => u.Orders.Any());

        var total = await query.CountAsync();
        var rows = await query
            .Select(u => new CustomerRow
            {
                Id = u.Id,
                Name = (u.FirstName + " " + u.LastName).Trim(),
                Email = u.Email ?? "",
                Phone = u.PhoneNumber,
                Orders = u.Orders.Count,
                Spend = u.Orders.Where(o => o.PaidAt != null).Sum(o => (decimal?)o.Total) ?? 0,
                LastOrder = u.Orders.Max(o => (DateTime?)o.CreatedAt)
            })
            .OrderByDescending(c => c.Spend)
            .Skip((page - 1) * PageSize).Take(PageSize).ToListAsync();

        ViewBag.Query = q; ViewBag.Page = page;
        ViewBag.TotalPages = (int)Math.Ceiling(total / (double)PageSize); ViewBag.Total = total;
        return View(rows);
    }

    public async Task<IActionResult> CustomerDetail(string id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null) return NotFound();
        ViewData["Title"] = user.FullName;

        var orders = await _db.Orders
            .Where(o => o.UserId == id || o.CustomerUserId == id)
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => new SaleRow
            {
                Id = o.Id, OrderNumber = o.OrderNumber, Channel = o.Channel.ToString(),
                Status = o.Status.ToString(), Total = o.Total, Paid = o.PaidAt != null,
                PaymentProvider = o.PaymentProvider, When = o.CreatedAt
            }).Take(50).ToListAsync();

        ViewBag.Orders = orders;
        ViewBag.Spend = orders.Where(o => o.Paid).Sum(o => o.Total);
        ViewBag.Loyalty = await _db.LoyaltyAccounts.Where(l => l.UserId == id)
            .Select(l => (int?)l.PointsBalance).FirstOrDefaultAsync();
        return View(user);
    }

    public async Task<IActionResult> Discounts()
    {
        ViewData["Title"] = "Discounts";
        var rows = await _db.DiscountCodes.OrderByDescending(d => d.Id).ToListAsync();
        return View(rows);
    }
}

public class CustomerRow
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Phone { get; set; }
    public int Orders { get; set; }
    public decimal Spend { get; set; }
    public DateTime? LastOrder { get; set; }
}
