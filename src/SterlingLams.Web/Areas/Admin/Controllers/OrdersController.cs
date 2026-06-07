using System;
using System.Linq;
using System.Text;
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
        protected override string Section => "Orders";

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

            // Count per status for tab badges
            var allStatuses = new[] { "Pending","Confirmed","Processing","ReadyForPickup","Shipped","Delivered","Cancelled" };
            var counts = await _db.Orders
                .GroupBy(o => o.Status)
                .Select(g => new { Status = g.Key.ToString(), Count = g.Count() })
                .ToListAsync();
            var statusCounts = allStatuses.ToDictionary(s => s, s => counts.FirstOrDefault(c => c.Status == s)?.Count ?? 0);
            statusCounts[""] = await _db.Orders.CountAsync(); // "All" tab

            var vm = new AdminOrderListViewModel
            {
                Orders = orders,
                StatusFilter = status,
                SearchQuery = q,
                CurrentPage = page,
                TotalPages = (int)Math.Ceiling(total / (double)PageSize),
                StatusCounts = statusCounts
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
                var old = order.Status.ToString();
                order.Status = newStatus;
                order.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                await LogAsync("Update", "Order", order.Id.ToString(),
                    $"Order {order.OrderNumber} status: {old} → {status}");
                TempData["Success"] = $"Order {order.OrderNumber} updated to {status}.";
            }

            return RedirectToAction(nameof(Detail), new { id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveNote(int id, string adminNotes)
        {
            var order = await _db.Orders.FindAsync(id);
            if (order == null) return NotFound();

            order.AdminNotes = adminNotes?.Trim();
            order.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            TempData["Success"] = "Note saved.";
            return RedirectToAction(nameof(Detail), new { id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveTracking(int id, string trackingNumber)
        {
            var order = await _db.Orders.FindAsync(id);
            if (order == null) return NotFound();

            order.TrackingNumber = trackingNumber?.Trim();
            order.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            await LogAsync("Update", "Order", order.Id.ToString(),
                $"Set tracking number for {order.OrderNumber}: {order.TrackingNumber}");

            TempData["Success"] = "Tracking number saved.";
            return RedirectToAction(nameof(Detail), new { id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BulkUpdateStatus(int[] orderIds, string status)
        {
            if (orderIds == null || orderIds.Length == 0 || string.IsNullOrWhiteSpace(status))
                return RedirectToAction(nameof(Index));

            if (!Enum.TryParse<OrderStatus>(status, out var newStatus))
                return RedirectToAction(nameof(Index));

            var orders = await _db.Orders
                .Where(o => orderIds.Contains(o.Id))
                .ToListAsync();

            foreach (var o in orders)
            {
                o.Status = newStatus;
                o.UpdatedAt = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync();
            await LogAsync("Update", "Order", null,
                $"Bulk updated {orders.Count} order(s) to {status}");
            TempData["Success"] = $"{orders.Count} order(s) updated to {status}.";
            return RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> ExportCsv(string status = "", string q = "")
        {
            var query = _db.Orders.Include(o => o.User).AsQueryable();

            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<OrderStatus>(status, out var statusEnum))
                query = query.Where(o => o.Status == statusEnum);

            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(o =>
                    o.OrderNumber.Contains(q) ||
                    o.User.FirstName.Contains(q) ||
                    o.User.LastName.Contains(q) ||
                    o.User.Email!.Contains(q));

            var orders = await query
                .OrderByDescending(o => o.CreatedAt)
                .Select(o => new
                {
                    o.OrderNumber,
                    CustomerName = o.User.FirstName + " " + o.User.LastName,
                    CustomerEmail = o.User.Email ?? "",
                    o.Total,
                    o.Subtotal,
                    o.DeliveryFee,
                    Status = o.Status.ToString(),
                    Fulfillment = o.FulfillmentType.ToString(),
                    o.IsPaid,
                    PaymentRef = o.PaymentReference ?? "",
                    o.ErpNextInvoiceName,
                    CreatedAt = o.CreatedAt.ToString("yyyy-MM-dd HH:mm")
                })
                .ToListAsync();

            var sb = new StringBuilder();
            sb.AppendLine("Order #,Customer Name,Customer Email,Total,Subtotal,Delivery Fee,Status,Fulfillment,Paid,Payment Ref,ERPNext Order,Created At");

            foreach (var o in orders)
            {
                sb.AppendLine($"\"{o.OrderNumber}\",\"{o.CustomerName}\",\"{o.CustomerEmail}\",{o.Total},{o.Subtotal},{o.DeliveryFee},{o.Status},{o.Fulfillment},{o.IsPaid},\"{o.PaymentRef}\",\"{o.ErpNextInvoiceName ?? ""}\",\"{o.CreatedAt}\"");
            }

            await LogAsync("Export", "Order", null, $"Exported {orders.Count} order(s) to CSV");

            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return File(bytes, "text/csv", $"orders_{DateTime.UtcNow:yyyyMMdd}.csv");
        }
    }
}
