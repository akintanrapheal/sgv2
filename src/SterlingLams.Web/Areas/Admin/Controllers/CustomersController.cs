using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    public class CustomersController : AdminBaseController
    {
        protected override string Section => "Customers";

        private readonly ApplicationDbContext _db;
        private const int PageSize = 30;

        public CustomersController(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task<IActionResult> Index(string q = "", int page = 1)
        {
            ViewData["Title"] = "Customers";

            var query = _db.Users.AsQueryable();

            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(u =>
                    EF.Functions.ILike(u.FirstName + " " + u.LastName, $"%{q}%") ||
                    EF.Functions.ILike(u.Email!, $"%{q}%"));

            var total = await query.CountAsync();

            var customers = await query
                .OrderByDescending(u => u.CreatedAt)
                .Skip((page - 1) * PageSize)
                .Take(PageSize)
                .Select(u => new AdminCustomerRow
                {
                    Id = u.Id,
                    FullName = u.FirstName + " " + u.LastName,
                    Email = u.Email ?? "",
                    Phone = u.PhoneNumber,
                    OrderCount = u.Orders.Count,
                    TotalSpend = u.Orders.Where(o => o.IsPaid).Sum(o => (decimal?)o.Total) ?? 0,
                    JoinedAt = u.CreatedAt,
                    LastOrderAt = u.Orders.Any() ? u.Orders.Max(o => (DateTime?)o.CreatedAt) : null
                })
                .ToListAsync();

            var vm = new AdminCustomerListViewModel
            {
                Customers = customers,
                SearchQuery = q,
                CurrentPage = page,
                TotalPages = (int)Math.Ceiling(total / (double)PageSize)
            };

            return View(vm);
        }

        public async Task<IActionResult> Detail(string id)
        {
            ViewData["Title"] = "Customer";

            var user = await _db.Users.FindAsync(id);
            if (user == null) return NotFound();

            var orders = await _db.Orders
                .Where(o => o.UserId == id)
                .OrderByDescending(o => o.CreatedAt)
                .Take(10)
                .Select(o => new RecentOrderRow
                {
                    OrderNumber = o.OrderNumber,
                    CustomerName = user.FirstName + " " + user.LastName,
                    Total = o.Total,
                    Status = o.Status.ToString(),
                    CreatedAt = o.CreatedAt
                })
                .ToListAsync();

            var vm = new AdminCustomerDetailViewModel
            {
                Id = user.Id,
                FullName = user.FullName,
                Email = user.Email ?? "",
                Phone = user.PhoneNumber,
                JoinedAt = user.CreatedAt,
                OrderCount = await _db.Orders.CountAsync(o => o.UserId == id),
                TotalSpend = await _db.Orders
                    .Where(o => o.UserId == id && o.IsPaid)
                    .SumAsync(o => (decimal?)o.Total) ?? 0,
                RecentOrders = orders
            };

            return View(vm);
        }

        public async Task<IActionResult> ExportCsv(string q = "")
        {
            var query = _db.Users.AsQueryable();
            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(u =>
                    EF.Functions.ILike(u.FirstName + " " + u.LastName, $"%{q}%") ||
                    EF.Functions.ILike(u.Email!, $"%{q}%"));

            var customers = await query
                .OrderByDescending(u => u.CreatedAt)
                .Select(u => new
                {
                    FullName  = u.FirstName + " " + u.LastName,
                    u.Email,
                    Phone     = u.PhoneNumber ?? "",
                    Orders    = u.Orders.Count,
                    TotalSpend = u.Orders.Where(o => o.IsPaid).Sum(o => (decimal?)o.Total) ?? 0,
                    Joined    = u.CreatedAt.ToString("yyyy-MM-dd")
                })
                .ToListAsync();

            var sb = new StringBuilder();
            sb.AppendLine("Full Name,Email,Phone,Orders,Total Spend,Joined");
            foreach (var c in customers)
                sb.AppendLine($"\"{c.FullName}\",\"{c.Email}\",\"{c.Phone}\",{c.Orders},{c.TotalSpend},\"{c.Joined}\"");

            await LogAsync("Export", "Customer", null, $"Exported {customers.Count} customer record(s) to CSV");

            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return File(bytes, "text/csv", $"customers_{DateTime.UtcNow:yyyyMMdd}.csv");
        }
    }
}
