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
    public class DashboardController : AdminBaseController
    {
        private readonly ApplicationDbContext _db;

        public DashboardController(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task<IActionResult> Index()
        {
            ViewData["Title"] = "Dashboard";

            var today = DateTime.UtcNow.Date;
            var monthStart = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc);

            var vm = new DashboardViewModel
            {
                RevenueToday = await _db.Orders
                    .Where(o => o.CreatedAt >= today && o.IsPaid)
                    .SumAsync(o => (decimal?)o.Total) ?? 0,

                RevenueThisMonth = await _db.Orders
                    .Where(o => o.CreatedAt >= monthStart && o.IsPaid)
                    .SumAsync(o => (decimal?)o.Total) ?? 0,

                OrdersToday = await _db.Orders
                    .CountAsync(o => o.CreatedAt >= today),

                OrdersPending = await _db.Orders
                    .CountAsync(o => o.Status == OrderStatus.Pending || o.Status == OrderStatus.Processing),

                TotalProducts = await _db.Products.CountAsync(p => p.IsActive),

                LowStockAlerts = await _db.StoreInventories
                    .CountAsync(si => si.QuantityOnHand > 0 && si.QuantityOnHand < 3),

                RecentOrders = await _db.Orders
                    .Include(o => o.User)
                    .OrderByDescending(o => o.CreatedAt)
                    .Take(10)
                    .Select(o => new RecentOrderRow
                    {
                        OrderNumber = o.OrderNumber,
                        CustomerName = o.User.FullName,
                        Total = o.Total,
                        Status = o.Status.ToString(),
                        CreatedAt = o.CreatedAt
                    })
                    .ToListAsync(),

                LowStockItems = await _db.StoreInventories
                    .Include(si => si.Product)
                    .Include(si => si.Store)
                    .Where(si => si.QuantityOnHand > 0 && si.QuantityOnHand < 3)
                    .OrderBy(si => si.QuantityOnHand)
                    .Take(8)
                    .Select(si => new LowStockRow
                    {
                        ProductName = si.Product.Name,
                        StoreName = si.Store.Name,
                        Quantity = si.QuantityOnHand
                    })
                    .ToListAsync()
            };

            return View(vm);
        }
    }
}
