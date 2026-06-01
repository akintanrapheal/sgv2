using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    public class OrdersController : AdminBaseController
    {
        private readonly ApplicationDbContext _db;
        private const int PageSize = 25;

        public OrdersController(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task<IActionResult> Index(string status = "", string q = "", int page = 1)
        {
            ViewData["Title"] = "Orders";

            var query = _db.Orders
                .Include(o => o.User)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<OrderStatus>(status, out var statusEnum))
                query = query.Where(o => o.Status == statusEnum);

            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(o =>
                    o.OrderNumber.Contains(q) ||
                    o.User.FirstName.Contains(q) ||
                    o.User.LastName.Contains(q) ||
                    o.User.Email!.Contains(q));

            var total = await query.CountAsync();
            var orders = await query
                .OrderByDescending(o => o.CreatedAt)
                .Skip((page - 1) * PageSize)
                .Take(PageSize)
                .Select(o => new AdminOrderRow
                {
                    Id = o.Id,
                    OrderNumber = o.OrderNumber,
                    CustomerName = o.User.FirstName + " " + o.User.LastName,
                    CustomerEmail = o.User.Email ?? "",
                    Total = o.Total,
                    Status = o.Status.ToString(),
                    IsPaid = o.IsPaid,
                    FulfillmentType = o.FulfillmentType.ToString(),
                    CreatedAt = o.CreatedAt
                })
                .ToListAsync();

            var vm = new AdminOrderListViewModel
            {
                Orders = orders,
                StatusFilter = status,
                SearchQuery = q,
                CurrentPage = page,
                TotalPages = (int)Math.Ceiling(total / (double)PageSize)
            };

            return View(vm);
        }

        public async Task<IActionResult> Detail(int id)
        {
            ViewData["Title"] = "Order Detail";

            var order = await _db.Orders
                .Include(o => o.Items).ThenInclude(i => i.Product)
                .Include(o => o.Items).ThenInclude(i => i.ProductVariant)
                .Include(o => o.PickupStore)
                .Include(o => o.User)
                .FirstOrDefaultAsync(o => o.Id == id);

            if (order == null) return NotFound();

            var vm = new AdminOrderDetailViewModel
            {
                Order = order,
                CustomerName = order.User.FullName,
                CustomerEmail = order.User.Email ?? ""
            };

            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateStatus(int id, string status)
        {
            var order = await _db.Orders.FindAsync(id);
            if (order == null) return NotFound();

            if (Enum.TryParse<OrderStatus>(status, out var newStatus))
            {
                order.Status = newStatus;
                order.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                TempData["Success"] = $"Order {order.OrderNumber} updated to {status}.";
            }

            return RedirectToAction(nameof(Detail), new { id });
        }
    }
}
