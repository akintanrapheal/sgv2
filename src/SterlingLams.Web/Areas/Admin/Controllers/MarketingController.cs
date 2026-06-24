using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    // Surfaces signals the storefront already collects but were previously invisible: abandoned
    // carts and back-in-stock requests. Read-only lists to act on / measure recovery.
    public class MarketingController : AdminBaseController
    {
        protected override string Section => "Marketing";

        private readonly ApplicationDbContext _db;
        public MarketingController(ApplicationDbContext db) => _db = db;

        public async Task<IActionResult> Index(string tab = "carts")
        {
            ViewData["Title"] = "Marketing";

            var vm = new MarketingViewModel
            {
                Tab = tab == "backinstock" ? "backinstock" : "carts",
                AbandonedCount = await _db.AbandonedCarts.CountAsync(c => c.RecoveredAt == null),
                BackInStockCount = await _db.BackInStockRequests.CountAsync(r => r.NotifiedAt == null)
            };

            if (vm.Tab == "backinstock")
            {
                vm.BackInStock = await _db.BackInStockRequests
                    .Include(r => r.Product)
                    .OrderByDescending(r => r.NotifiedAt == null) // open first
                    .ThenByDescending(r => r.Id)
                    .Take(200)
                    .Select(r => new BackInStockRow
                    {
                        Email = r.Email,
                        ProductName = r.Product.Name,
                        ProductId = r.ProductId,
                        CreatedAt = r.CreatedAt,
                        NotifiedAt = r.NotifiedAt
                    })
                    .ToListAsync();
            }
            else
            {
                var carts = await _db.AbandonedCarts
                    .OrderByDescending(c => c.CreatedAt)
                    .Take(200)
                    .ToListAsync();
                vm.Carts = carts.Select(c => new AbandonedCartRow
                {
                    Email = c.Email,
                    ItemCount = c.ItemCount,
                    Subtotal = c.Subtotal,
                    CreatedAt = c.CreatedAt,
                    EmailedAt = c.EmailedAt,
                    RecoveredAt = c.RecoveredAt,
                    Items = SummariseItems(c.ItemsJson)
                }).ToList();
            }

            return View(vm);
        }

        // Pulls a short "2× Gold Ring, 1× …" summary out of the stored cart JSON, defensively.
        private static string SummariseItems(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return "";
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return "";
                var parts = new List<string>();
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    string? name = el.TryGetProperty("name", out var n) ? n.GetString()
                        : el.TryGetProperty("Name", out var n2) ? n2.GetString() : null;
                    int qty = el.TryGetProperty("quantity", out var q) && q.TryGetInt32(out var qi) ? qi
                        : el.TryGetProperty("qty", out var q2) && q2.TryGetInt32(out var qi2) ? qi2 : 1;
                    if (!string.IsNullOrWhiteSpace(name)) parts.Add($"{qty}× {name}");
                    if (parts.Count >= 4) { parts.Add("…"); break; }
                }
                return string.Join(", ", parts);
            }
            catch { return ""; }
        }
    }
}
