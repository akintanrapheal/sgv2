using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    public class CustomersController : AdminBaseController
    {
        protected override string Section => "Customers";

        private readonly ApplicationDbContext _db;
        private readonly ILoyaltyService _loyalty;
        private const int PageSize = 30;

        public CustomersController(ApplicationDbContext db, ILoyaltyService loyalty)
        {
            _db = db;
            _loyalty = loyalty;
        }

        public async Task<IActionResult> Index(string q = "", string segment = "", string tag = "", int page = 1)
        {
            ViewData["Title"] = "Customers";

            var query = _db.Users.AsQueryable();

            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(u =>
                    EF.Functions.ILike(u.FirstName + " " + u.LastName, $"%{q}%") ||
                    EF.Functions.ILike(u.Email!, $"%{q}%"));

            if (!string.IsNullOrWhiteSpace(tag))
                query = query.Where(u => u.Tags != null && EF.Functions.ILike(u.Tags, $"%{tag}%"));

            // Segment filters mirror the derived badges on AdminCustomerRow.
            var lapsedCutoff = DateTime.UtcNow.AddDays(-CustomerSegments.LapsedDays);
            query = segment switch
            {
                "vip" => query.Where(u => (u.Orders.Where(o => o.IsPaid).Sum(o => (decimal?)o.Total) ?? 0) >= CustomerSegments.VipSpend),
                "repeat" => query.Where(u => u.Orders.Count >= 2),
                "lapsed" => query.Where(u => u.Orders.Any() && u.Orders.Max(o => o.CreatedAt) < lapsedCutoff),
                "new" => query.Where(u => u.Orders.Count <= 1),
                _ => query
            };

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
                    LastOrderAt = u.Orders.Any() ? u.Orders.Max(o => (DateTime?)o.CreatedAt) : null,
                    Tags = u.Tags
                })
                .ToListAsync();

            var vm = new AdminCustomerListViewModel
            {
                Customers = customers,
                SearchQuery = q,
                SegmentFilter = segment,
                TagFilter = tag,
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
                RecentOrders = orders,
                Tags = user.Tags,
                LoyaltyBalance = await _loyalty.GetBalanceAsync(id),
                LoyaltyEntries = await _db.PointsLedgerEntries
                    .Where(p => p.Account.UserId == id)
                    .OrderByDescending(p => p.Id).Take(15).ToListAsync()
            };

            return View(vm);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> AdjustLoyalty(string id, int points, string? reason)
        {
            var user = await _db.Users.FindAsync(id);
            if (user == null) return NotFound();
            if (points == 0)
            {
                TempData["Error"] = "Enter a non-zero number of points.";
                return RedirectToAction(nameof(Detail), new { id });
            }
            var balance = await _loyalty.AdjustAsync(id, points, reason ?? "Manual adjustment");
            await LogAsync("LoyaltyAdjust", "Customer", id,
                $"Adjusted {user.FullName}'s points by {(points > 0 ? "+" : "")}{points} — new balance {balance}{(string.IsNullOrWhiteSpace(reason) ? "" : $" ({reason!.Trim()})")}");
            TempData["Success"] = $"Points adjusted. New balance: {balance}.";
            return RedirectToAction(nameof(Detail), new { id });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveTags(string id, string? tags)
        {
            var user = await _db.Users.FindAsync(id);
            if (user == null) return NotFound();
            // Normalise: trim, drop blanks, de-dupe (case-insensitive), comma-join.
            var cleaned = string.Join(", ", (tags ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase));
            user.Tags = cleaned.Length == 0 ? null : cleaned;
            await _db.SaveChangesAsync();
            await LogAsync("Update", "Customer", id, $"Updated tags for {user.FullName}: {user.Tags ?? "(none)"}");
            TempData["Success"] = "Tags saved.";
            return RedirectToAction(nameof(Detail), new { id });
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
