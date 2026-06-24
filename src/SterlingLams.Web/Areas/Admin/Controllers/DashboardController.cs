using System;
using System.Collections.Generic;
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
        protected override string Section => "Dashboard";

        private readonly ApplicationDbContext _db;

        public DashboardController(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task<IActionResult> Index(int days = 30)
        {
            ViewData["Title"] = "Dashboard";
            if (days != 7 && days != 30 && days != 90) days = 30;

            var now = DateTime.UtcNow;
            var today = now.Date;
            var yesterday = today.AddDays(-1);
            var monthStart = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            // Last month to the SAME elapsed point, so month-to-date compares like-for-like.
            var elapsed = now - monthStart;
            var lmStart = monthStart.AddMonths(-1);
            var lmEnd = lmStart + elapsed;

            var vm = new DashboardViewModel
            {
                RevenueToday = await _db.Orders
                    .Where(o => o.CreatedAt >= today && o.IsPaid)
                    .SumAsync(o => (decimal?)o.Total) ?? 0,

                RevenueYesterday = await _db.Orders
                    .Where(o => o.CreatedAt >= yesterday && o.CreatedAt < today && o.IsPaid)
                    .SumAsync(o => (decimal?)o.Total) ?? 0,

                RevenueThisMonth = await _db.Orders
                    .Where(o => o.CreatedAt >= monthStart && o.IsPaid)
                    .SumAsync(o => (decimal?)o.Total) ?? 0,

                RevenueLastMonthMtd = await _db.Orders
                    .Where(o => o.CreatedAt >= lmStart && o.CreatedAt < lmEnd && o.IsPaid)
                    .SumAsync(o => (decimal?)o.Total) ?? 0,

                OrdersToday = await _db.Orders
                    .CountAsync(o => o.CreatedAt >= today),

                OrdersYesterday = await _db.Orders
                    .CountAsync(o => o.CreatedAt >= yesterday && o.CreatedAt < today),

                OrdersPending = await _db.Orders
                    .CountAsync(o => o.Status == OrderStatus.Pending || o.Status == OrderStatus.Processing),

                TotalProducts = await _db.Products.CountAsync(p => p.IsActive),

                TotalCustomers = await _db.Users.CountAsync(),

                // Use each product's own threshold (floored at 1), consistent with the rest of the system.
                LowStockAlerts = await _db.StoreInventories
                    .CountAsync(si => si.QuantityOnHand > 0
                        && si.QuantityOnHand < (si.Product.LowStockThreshold < 1 ? 1 : si.Product.LowStockThreshold)),

                RecentOrders = await _db.Orders
                    .Include(o => o.User)
                    .OrderByDescending(o => o.CreatedAt)
                    .Take(10)
                    .Select(o => new RecentOrderRow
                    {
                        Id = o.Id,
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
                    .Where(si => si.QuantityOnHand > 0
                        && si.QuantityOnHand < (si.Product.LowStockThreshold < 1 ? 1 : si.Product.LowStockThreshold))
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

            // Average order value (paid orders), this month vs last month-to-date.
            var paidThisMonth = await _db.Orders.CountAsync(o => o.IsPaid && o.CreatedAt >= monthStart);
            var paidLmMtd = await _db.Orders.CountAsync(o => o.IsPaid && o.CreatedAt >= lmStart && o.CreatedAt < lmEnd);
            vm.AovThisMonth = paidThisMonth > 0 ? vm.RevenueThisMonth / paidThisMonth : 0;
            vm.AovLastMonthMtd = paidLmMtd > 0 ? vm.RevenueLastMonthMtd / paidLmMtd : 0;

            // Channel split (online vs in-store POS) for this month's paid revenue.
            var byChannel = await _db.Orders
                .Where(o => o.IsPaid && o.CreatedAt >= monthStart)
                .GroupBy(o => o.Channel)
                .Select(g => new { g.Key, Total = g.Sum(o => o.Total) })
                .ToListAsync();
            vm.RevenueOnlineMonth = byChannel.FirstOrDefault(c => c.Key == OrderChannel.Online)?.Total ?? 0;
            vm.RevenuePosMonth = byChannel.FirstOrDefault(c => c.Key == OrderChannel.Pos)?.Total ?? 0;

            // Revenue chart for selected day range
            var chartStart = today.AddDays(-(days - 1));
            var revenueByDay = await _db.Orders
                .Where(o => o.IsPaid && o.CreatedAt >= chartStart)
                .GroupBy(o => o.CreatedAt.Date)
                .Select(g => new { Date = g.Key, Amount = g.Sum(o => o.Total) })
                .ToListAsync();

            var dailyRevenue = new List<DailyRevenueRow>();
            for (int i = days - 1; i >= 0; i--)
            {
                var date = today.AddDays(-i);
                var amount = revenueByDay.FirstOrDefault(r => r.Date == date)?.Amount ?? 0;
                dailyRevenue.Add(new DailyRevenueRow { Date = date.ToString("MMM dd"), Amount = amount });
            }
            vm.DailyRevenue = dailyRevenue;
            vm.ChartDays = days;

            // Top 5 selling products (by units sold, last 90 days)
            var since90 = today.AddDays(-90);
            vm.TopProducts = await _db.OrderItems
                .Include(i => i.Product).ThenInclude(p => p.Category)
                .Where(i => i.Product.IsActive && i.Order.IsPaid && i.Order.CreatedAt >= since90)
                .GroupBy(i => new { i.ProductId, i.Product.Name, CategoryName = i.Product.Category.Name })
                .Select(g => new TopProductRow
                {
                    ProductName  = g.Key.Name,
                    CategoryName = g.Key.CategoryName,
                    UnitsSold    = g.Sum(i => i.Quantity),
                    Revenue      = g.Sum(i => i.Quantity * i.UnitPrice)
                })
                .OrderByDescending(r => r.UnitsSold)
                .Take(5)
                .ToListAsync();

            // Orders by status (last 90 days) for the breakdown doughnut.
            vm.OrdersByStatus = (await _db.Orders
                    .Where(o => o.CreatedAt >= since90)
                    .GroupBy(o => o.Status)
                    .Select(g => new { g.Key, Count = g.Count() })
                    .ToListAsync())
                .OrderByDescending(g => g.Count)
                .Select(g => new StatusSliceRow { Status = g.Key.ToString(), Count = g.Count })
                .ToList();

            return View(vm);
        }
    }
}
