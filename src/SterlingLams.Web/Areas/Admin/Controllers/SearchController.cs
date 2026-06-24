using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    // Global top-bar search across orders, customers and products. Gated by the universal "Dashboard"
    // section so any backend user can reach it; each result group is only queried/shown when the user
    // has access to that section, so it never surfaces (or links to) areas they can't open.
    public class SearchController : AdminBaseController
    {
        protected override string Section => "Dashboard";

        private readonly ApplicationDbContext _db;
        private readonly IPermissionService _perms;
        public SearchController(ApplicationDbContext db, IPermissionService perms)
        {
            _db = db;
            _perms = perms;
        }

        public async Task<IActionResult> Index(string q = "")
        {
            q = (q ?? "").Trim();
            ViewData["Title"] = string.IsNullOrEmpty(q) ? "Search" : $"Search — \"{q}\"";

            var vm = new AdminSearchViewModel { Query = q };
            vm.CanOrders = await _perms.CanAccessAsync(User, "Orders");
            vm.CanCustomers = await _perms.CanAccessAsync(User, "Customers");
            vm.CanProducts = await _perms.CanAccessAsync(User, "Products");

            if (q.Length < 2) return View(vm);
            var like = $"%{q}%";

            if (vm.CanOrders)
            {
                vm.Orders = await _db.Orders
                    .Where(o => EF.Functions.ILike(o.OrderNumber, like)
                             || EF.Functions.ILike(o.User.FirstName + " " + o.User.LastName, like)
                             || EF.Functions.ILike(o.User.Email ?? "", like)
                             || EF.Functions.ILike(o.User.PhoneNumber ?? "", like))
                    .OrderByDescending(o => o.CreatedAt).Take(8)
                    .Select(o => new SearchOrderRow
                    {
                        Id = o.Id, OrderNumber = o.OrderNumber,
                        CustomerName = (o.User.FirstName + " " + o.User.LastName).Trim(),
                        Total = o.Total, Status = o.Status.ToString(), CreatedAt = o.CreatedAt
                    }).ToListAsync();
            }

            if (vm.CanCustomers)
            {
                vm.Customers = await _db.Users
                    .Where(u => EF.Functions.ILike(u.FirstName + " " + u.LastName, like)
                             || EF.Functions.ILike(u.Email ?? "", like)
                             || EF.Functions.ILike(u.PhoneNumber ?? "", like))
                    .OrderByDescending(u => u.CreatedAt).Take(8)
                    .Select(u => new SearchCustomerRow
                    {
                        Id = u.Id, FullName = (u.FirstName + " " + u.LastName).Trim(),
                        Email = u.Email ?? "", Phone = u.PhoneNumber
                    }).ToListAsync();
            }

            if (vm.CanProducts)
            {
                vm.Products = await _db.Products
                    .Where(p => EF.Functions.ILike(p.Name, like)
                             || EF.Functions.ILike(p.Sku ?? "", like)
                             || EF.Functions.ILike(p.Barcode ?? "", like))
                    .OrderBy(p => p.Name).Take(8)
                    .Select(p => new SearchProductRow
                    {
                        Id = p.Id, Name = p.Name, Sku = p.Sku, Price = p.Price, IsActive = p.IsActive
                    }).ToListAsync();
            }

            return View(vm);
        }
    }
}
