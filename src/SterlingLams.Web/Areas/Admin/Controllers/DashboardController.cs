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

            // Days are LAGOS days (see Services/ReportCalendar): "today" used to mean the UTC day,
            // so anything sold between midnight and 1am counted against yesterday.
            var now = DateTime.UtcNow;
            var todayLocal = SterlingLams.Web.Services.ReportCalendar.Today;
            var today = SterlingLams.Web.Services.ReportCalendar.StartOfDayUtc(todayLocal);
            var yesterday = SterlingLams.Web.Services.ReportCalendar.StartOfDayUtc(todayLocal.AddDays(-1));
            var monthStart = SterlingLams.Web.Services.ReportCalendar.StartOfDayUtc(
                new DateTime(todayLocal.Year, todayLocal.Month, 1));
            // Last month to the SAME elapsed point, so month-to-date compares like-for-like.
            var elapsed = now - monthStart;
            var lmStart = SterlingLams.Web.Services.ReportCalendar.StartOfDayUtc(
                new DateTime(todayLocal.Year, todayLocal.Month, 1).AddMonths(-1));
            var lmEnd = lmStart + elapsed;

            // Role ids that mark a user as staff (everything except the implicit Customer role).
            var staffRoleIds = await _db.Roles.Where(r => r.Name != "Customer").Select(r => r.Id).ToListAsync();

            var vm = new DashboardViewModel
            {
                // Revenue is dated by when the sale was PAID (falling back to creation for older rows),
                // so a transfer confirmed today counts today rather than on the day it was placed.
                RevenueToday = await _db.Orders
                    .Where(o => o.IsPaid && (o.PaidAt ?? o.CreatedAt) >= today)
                    .SumAsync(o => (decimal?)o.Total) ?? 0,

                RevenueYesterday = await _db.Orders
                    .Where(o => o.IsPaid && (o.PaidAt ?? o.CreatedAt) >= yesterday && (o.PaidAt ?? o.CreatedAt) < today)
                    .SumAsync(o => (decimal?)o.Total) ?? 0,

                RevenueThisMonth = await _db.Orders
                    .Where(o => o.IsPaid && (o.PaidAt ?? o.CreatedAt) >= monthStart)
                    .SumAsync(o => (decimal?)o.Total) ?? 0,

                RevenueLastMonthMtd = await _db.Orders
                    .Where(o => o.IsPaid && (o.PaidAt ?? o.CreatedAt) >= lmStart && (o.PaidAt ?? o.CreatedAt) < lmEnd)
                    .SumAsync(o => (decimal?)o.Total) ?? 0,

                OrdersToday = await _db.Orders
                    .CountAsync(o => o.CreatedAt >= today),

                OrdersYesterday = await _db.Orders
                    .CountAsync(o => o.CreatedAt >= yesterday && o.CreatedAt < today),

                OrdersPending = await _db.Orders
                    .CountAsync(o => o.Status == OrderStatus.Pending || o.Status == OrderStatus.Processing),

                TotalProducts = await _db.Products.CountAsync(p => p.IsActive),

                // Customers only — a user holding any backend role is staff and belongs on the Users
                // screen, not in this count.
                TotalCustomers = await _db.Users.CountAsync(u =>
                    !_db.UserRoles.Any(ur => ur.UserId == u.Id && staffRoleIds.Contains(ur.RoleId))),

                // At or below the product's own threshold (floored at 1) — "<=" matches the Stock
                // report and the storefront; this tile used "<" and so reported a different count.
                LowStockAlerts = await _db.StoreInventories
                    .CountAsync(si => si.QuantityOnHand > 0
                        && si.QuantityOnHand <= (si.Product.LowStockThreshold < 1 ? 1 : si.Product.LowStockThreshold)),

                RecentOrders = await _db.Orders
                    .Include(o => o.User)
                    .OrderByDescending(o => o.CreatedAt)
                    .Take(10)
                    .Select(o => new RecentOrderRow
                    {
                        Id = o.Id,
                        OrderNumber = o.OrderNumber,
                        // The buyer: on a POS sale Order.User is the cashier.
                        CustomerName = o.Channel == OrderChannel.Pos
                            ? (o.Customer != null ? (o.Customer.FirstName + " " + o.Customer.LastName).Trim() : "Walk-in")
                            : o.User.FullName,
                        Total = o.Total,
                        Status = o.Status.ToString(),
                        CreatedAt = o.CreatedAt
                    })
                    .ToListAsync(),

                LowStockItems = await _db.StoreInventories
                    .Include(si => si.Product)
                    .Include(si => si.Store)
                    .Where(si => si.QuantityOnHand > 0
                        && si.QuantityOnHand <= (si.Product.LowStockThreshold < 1 ? 1 : si.Product.LowStockThreshold))
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
            var paidThisMonth = await _db.Orders.CountAsync(o => o.IsPaid && (o.PaidAt ?? o.CreatedAt) >= monthStart);
            var paidLmMtd = await _db.Orders.CountAsync(o => o.IsPaid
                && (o.PaidAt ?? o.CreatedAt) >= lmStart && (o.PaidAt ?? o.CreatedAt) < lmEnd);
            vm.AovThisMonth = paidThisMonth > 0 ? vm.RevenueThisMonth / paidThisMonth : 0;
            vm.AovLastMonthMtd = paidLmMtd > 0 ? vm.RevenueLastMonthMtd / paidLmMtd : 0;

            // Channel split (online vs in-store POS) for this month's paid revenue.
            var byChannel = await _db.Orders
                .Where(o => o.IsPaid && (o.PaidAt ?? o.CreatedAt) >= monthStart)
                .GroupBy(o => o.Channel)
                .Select(g => new { g.Key, Total = g.Sum(o => o.Total) })
                .ToListAsync();
            vm.RevenueOnlineMonth = byChannel.FirstOrDefault(c => c.Key == OrderChannel.Online)?.Total ?? 0;
            vm.RevenuePosMonth = byChannel.FirstOrDefault(c => c.Key == OrderChannel.Pos)?.Total ?? 0;

            // Marketing signals collected by the storefront (surfaced under Admin → Marketing).
            vm.AbandonedCartsOpen = await _db.AbandonedCarts.CountAsync(c => c.RecoveredAt == null);
            vm.BackInStockOpen = await _db.BackInStockRequests.CountAsync(r => r.NotifiedAt == null);

            // Revenue chart for selected day range
            var chartStart = SterlingLams.Web.Services.ReportCalendar.StartOfDayUtc(todayLocal.AddDays(-(days - 1)));
            var revenueRows = await _db.Orders
                .Where(o => o.IsPaid && (o.PaidAt ?? o.CreatedAt) >= chartStart)
                .Select(o => new { When = o.PaidAt ?? o.CreatedAt, o.Total })
                .ToListAsync();
            // Bucketed on the Lagos day in memory — the timestamps are stored UTC.
            var revenueByDay = revenueRows
                .GroupBy(o => SterlingLams.Web.Services.ReportCalendar.LocalDay(o.When))
                .ToDictionary(g => g.Key, g => g.Sum(o => o.Total));

            var dailyRevenue = new List<DailyRevenueRow>();
            for (int i = days - 1; i >= 0; i--)
            {
                var date = todayLocal.AddDays(-i);
                dailyRevenue.Add(new DailyRevenueRow
                {
                    Date = date.ToString("MMM dd"),
                    Amount = revenueByDay.GetValueOrDefault(date, 0)
                });
            }
            vm.DailyRevenue = dailyRevenue;
            vm.ChartDays = days;

            // Top 5 selling products (by units sold, last 90 days)
            var since90 = SterlingLams.Web.Services.ReportCalendar.StartOfDayUtc(todayLocal.AddDays(-90));
            vm.TopProducts = await _db.OrderItems
                .Include(i => i.Product).ThenInclude(p => p.Category)
                .Where(i => i.Product.IsActive && i.Order.IsPaid && (i.Order.PaidAt ?? i.Order.CreatedAt) >= since90)
                .GroupBy(i => new { i.ProductId, i.Product.Name, CategoryName = i.Product.Category.Name })
                .Select(g => new TopProductRow
                {
                    ProductName  = g.Key.Name,
                    CategoryName = g.Key.CategoryName,
                    UnitsSold    = g.Sum(i => i.Quantity),
                    // Net of line discounts, matching the Best Sellers report.
                    Revenue      = g.Sum(i => i.Quantity * i.UnitPrice - i.DiscountAmount)
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
