using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Controllers;

// No class-level [AllowAnonymous] — each action declares its own policy
public class TillController : Controller
{
    private const string RegisterCookie = "till_register";

    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;
    private readonly SignInManager<ApplicationUser> _signIn;
    private readonly IPasswordHasher<ApplicationUser> _hasher;

    public TillController(ApplicationDbContext db, IStockService stock,
        SignInManager<ApplicationUser> signIn, IPasswordHasher<ApplicationUser> hasher)
    {
        _db = db;
        _stock = stock;
        _signIn = signIn;
        _hasher = hasher;
    }

    private async Task<Register?> BoundRegisterAsync()
    {
        if (int.TryParse(Request.Cookies[RegisterCookie], out var id))
            return await _db.Registers.Include(r => r.Store)
                .FirstOrDefaultAsync(r => r.Id == id && r.IsActive);
        return null;
    }

    private Task<TillSession?> OpenSessionAsync(int registerId) =>
        _db.TillSessions.FirstOrDefaultAsync(s => s.RegisterId == registerId && s.ClosedAt == null);

    [AllowAnonymous]
    public async Task<IActionResult> Index()
    {
        var register = await BoundRegisterAsync();
        if (register == null)
        {
            var registers = await _db.Registers.Where(r => r.IsActive)
                .Include(r => r.Store).OrderBy(r => r.Name).ToListAsync();
            return View("PickRegister", registers);
        }

        ViewData["Register"] = register;

        if (!(User.Identity?.IsAuthenticated ?? false))
        {
            var cashiers = await _db.Users.Where(u => u.PinHash != null)
                .OrderBy(u => u.FirstName)
                .Select(u => new TillCashier { Id = u.Id, Name = (u.FirstName + " " + u.LastName).Trim() })
                .ToListAsync();
            return View("Login", cashiers);
        }

        var session = await OpenSessionAsync(register.Id);
        if (session == null) return View("OpenTill", register);

        // Pass display name so Sell view shows first name rather than email
        var uid = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var cashier = uid != null
            ? await _db.Users.Where(u => u.Id == uid)
                .Select(u => (u.FirstName + " " + u.LastName).Trim())
                .FirstOrDefaultAsync()
            : null;
        ViewData["CashierName"] = string.IsNullOrWhiteSpace(cashier) ? User.Identity?.Name : cashier;
        ViewData["Session"] = session;
        return View("Sell", register);
    }

    [Authorize, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> OpenSession(decimal openingFloat)
    {
        var register = await BoundRegisterAsync();
        if (register == null) return RedirectToAction(nameof(Index));
        if (await OpenSessionAsync(register.Id) == null)
        {
            _db.TillSessions.Add(new TillSession
            {
                RegisterId = register.Id,
                OpenedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "",
                OpenedAt = DateTime.UtcNow,
                OpeningFloat = Math.Max(0, openingFloat)
            });
            await _db.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Index));
    }

    [Authorize, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CloseSession(decimal countedCash, string? note)
    {
        var register = await BoundRegisterAsync();
        if (register == null) return Json(new { success = false, message = "Till not set up." });
        var session = await OpenSessionAsync(register.Id);
        if (session == null) return Json(new { success = false, message = "No open till." });

        session.ClosedAt = DateTime.UtcNow;
        session.ClosedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        session.CountedCash = countedCash;
        session.ClosingNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        await _db.SaveChangesAsync();
        return Json(new { success = true, sessionId = session.Id });
    }

    public class ZreportVm
    {
        public TillSession Session { get; set; } = null!;
        public int SaleCount { get; set; }
        public decimal TotalSales { get; set; }
        public decimal CashSales { get; set; }
        public decimal CardSales { get; set; }
        public decimal TransferSales { get; set; }
        public decimal RefundsTotal { get; set; }
        public decimal CashRefunds { get; set; }
        public decimal ExpectedCash { get; set; }
    }

    [Authorize]
    public async Task<IActionResult> Zreport(int id)
    {
        var session = await _db.TillSessions
            .Include(s => s.Register).ThenInclude(r => r.Store)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (session == null) return NotFound();

        var sales = await _db.Orders.Where(o => o.TillSessionId == id).ToListAsync();
        decimal SumOf(string m) => sales.Where(o => o.PaymentProvider == m).Sum(o => o.Total);
        var cash = SumOf("Cash");

        var refunds = await _db.Refunds.Where(r => r.TillSessionId == id).ToListAsync();
        var cashRefunds = refunds.Where(r => r.RefundMethod == "Cash").Sum(r => r.Amount);

        return View(new ZreportVm
        {
            Session = session,
            SaleCount = sales.Count,
            TotalSales = sales.Sum(o => o.Total),
            CashSales = cash,
            CardSales = SumOf("Card"),
            TransferSales = SumOf("Transfer"),
            RefundsTotal = refunds.Sum(r => r.Amount),
            CashRefunds = cashRefunds,
            ExpectedCash = session.OpeningFloat + cash - cashRefunds
        });
    }

    // ── Refunds / returns ─────────────────────────────────────────────────────
    [Authorize, HttpGet]
    public async Task<IActionResult> RefundLookup(string orderNumber)
    {
        orderNumber = (orderNumber ?? "").Trim();
        var order = await _db.Orders.Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.OrderNumber == orderNumber && o.Channel == OrderChannel.Pos);
        if (order == null) return Json(new { found = false });

        var refundIds = _db.Refunds.Where(r => r.OriginalOrderId == order.Id).Select(r => r.Id);
        var refunded = await _db.RefundItems.Where(ri => refundIds.Contains(ri.RefundId))
            .GroupBy(ri => new { ri.ProductId, ri.ProductVariantId })
            .Select(g => new { g.Key.ProductId, g.Key.ProductVariantId, Qty = g.Sum(x => x.Quantity) })
            .ToListAsync();
        int Done(int pid, int? vid) => refunded.FirstOrDefault(r => r.ProductId == pid && r.ProductVariantId == vid)?.Qty ?? 0;

        var items = order.Items.Select(i => new
        {
            productId = i.ProductId,
            variantId = i.ProductVariantId,
            name = i.ProductName,
            variantName = i.VariantName,
            unitPrice = i.UnitPrice,
            sold = i.Quantity,
            refundable = i.Quantity - Done(i.ProductId, i.ProductVariantId)
        }).ToList();

        return Json(new { found = true, orderId = order.Id, orderNumber = order.OrderNumber, total = order.Total, items });
    }

    public class RefundLine { public int ProductId { get; set; } public int? VariantId { get; set; } public int Quantity { get; set; } }
    public class RefundRequest
    {
        public int OrderId { get; set; }
        public string Method { get; set; } = "Cash";
        public string? Reason { get; set; }
        public List<RefundLine> Items { get; set; } = new();
    }

    [Authorize, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RefundProcess([FromBody] RefundRequest req)
    {
        var register = await BoundRegisterAsync();
        var order = await _db.Orders.Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == req.OrderId && o.Channel == OrderChannel.Pos);
        if (order == null) return Json(new { success = false, message = "Sale not found." });

        var lines = (req.Items ?? new()).Where(l => l.Quantity > 0).ToList();
        if (lines.Count == 0) return Json(new { success = false, message = "Choose at least one item to return." });

        var refundIds = _db.Refunds.Where(r => r.OriginalOrderId == order.Id).Select(r => r.Id);
        var refunded = await _db.RefundItems.Where(ri => refundIds.Contains(ri.RefundId))
            .GroupBy(ri => new { ri.ProductId, ri.ProductVariantId })
            .Select(g => new { g.Key.ProductId, g.Key.ProductVariantId, Qty = g.Sum(x => x.Quantity) })
            .ToListAsync();
        int Done(int pid, int? vid) => refunded.FirstOrDefault(r => r.ProductId == pid && r.ProductVariantId == vid)?.Qty ?? 0;

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var now = DateTime.UtcNow;
        var session = register != null ? await OpenSessionAsync(register.Id) : null;
        var storeId = order.PickupStoreId ?? register?.StoreId ?? 0;
        var refundNumber = $"REF-{now:yyMMdd}-{now:HHmmssfff}";

        await using var tx = await _db.Database.BeginTransactionAsync();

        var refund = new Refund
        {
            RefundNumber = refundNumber,
            OriginalOrderId = order.Id,
            RegisterId = register?.Id,
            TillSessionId = session?.Id,
            CashierUserId = userId,
            RefundMethod = req.Method,
            Reason = string.IsNullOrWhiteSpace(req.Reason) ? null : req.Reason.Trim(),
            CreatedAt = now
        };

        decimal amount = 0;
        foreach (var l in lines)
        {
            var oi = order.Items.FirstOrDefault(i => i.ProductId == l.ProductId && i.ProductVariantId == l.VariantId);
            if (oi == null) continue;
            var qty = Math.Min(l.Quantity, oi.Quantity - Done(l.ProductId, l.VariantId));
            if (qty <= 0) continue;

            amount += oi.UnitPrice * qty;
            refund.Items.Add(new RefundItem
            {
                ProductId = oi.ProductId, ProductVariantId = oi.ProductVariantId,
                ProductName = oi.ProductName, VariantName = oi.VariantName,
                Quantity = qty, UnitPrice = oi.UnitPrice
            });
            if (storeId > 0)
                await _stock.ApplyAsync(oi.ProductId, oi.ProductVariantId, storeId, qty,
                    StockMovementType.Return, refundNumber, userId: userId);
        }

        if (refund.Items.Count == 0) return Json(new { success = false, message = "Nothing left to refund on this sale." });

        refund.Amount = amount;
        _db.Refunds.Add(refund);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        return Json(new { success = true, refundNumber, amount });
    }

    [AllowAnonymous, HttpPost, ValidateAntiForgeryToken]
    public IActionResult SetRegister(int registerId)
    {
        Response.Cookies.Append(RegisterCookie, registerId.ToString(),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), HttpOnly = true, IsEssential = true });
        return RedirectToAction(nameof(Index));
    }

    [AllowAnonymous, HttpPost, ValidateAntiForgeryToken]
    public IActionResult ChangeRegister()
    {
        Response.Cookies.Delete(RegisterCookie);
        return RedirectToAction(nameof(Index));
    }

    [AllowAnonymous, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string userId, string pin)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.PinHash != null);
        if (user != null && !string.IsNullOrEmpty(pin) &&
            _hasher.VerifyHashedPassword(user, user.PinHash!, pin) != PasswordVerificationResult.Failed)
        {
            await _signIn.SignInAsync(user, isPersistent: false);
            return Json(new { success = true });
        }
        return Json(new { success = false, message = "Wrong PIN." });
    }

    [Authorize, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await _signIn.SignOutAsync();
        return RedirectToAction(nameof(Index));
    }

    // ── Discount reasons (configurable from admin) ────────────────────────────
    [Authorize, HttpGet]
    public async Task<IActionResult> DiscountReasons()
    {
        var reasons = await _db.PosDiscountReasons
            .Where(r => r.IsActive)
            .Include(r => r.Presets.OrderBy(p => p.SortOrder))
            .OrderBy(r => r.SortOrder)
            .Select(r => new
            {
                id = r.Id,
                name = r.Name,
                presets = r.Presets.Select(p => new { id = p.Id, label = p.Label, type = p.Type, value = p.Value })
            })
            .ToListAsync();
        return Json(reasons);
    }

    // ── Recent orders for this register (Orders tab) ──────────────────────────
    [Authorize, HttpGet]
    public async Task<IActionResult> RecentOrders()
    {
        var register = await BoundRegisterAsync();
        if (register == null) return Json(Array.Empty<object>());

        var orders = await _db.Orders
            .Where(o => o.Channel == OrderChannel.Pos && o.RegisterId == register.Id)
            .OrderByDescending(o => o.CreatedAt)
            .Take(25)
            .Select(o => new
            {
                id = o.Id,
                orderNumber = o.OrderNumber,
                total = o.Total,
                method = o.PaymentProvider,
                createdAt = o.CreatedAt,
                itemCount = o.Items.Sum(i => i.Quantity)
            })
            .ToListAsync();
        return Json(orders);
    }

    // ── Categories ────────────────────────────────────────────────────────────
    [Authorize, HttpGet]
    public async Task<IActionResult> Categories()
    {
        var cats = await _db.Categories.Where(c => c.IsActive)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new { id = c.Id, name = c.Name, imageUrl = c.ImageUrl })
            .ToListAsync();
        return Json(cats);
    }

    // ── Product search for the bound register's store ─────────────────────────
    [Authorize, HttpGet]
    public async Task<IActionResult> Search(string? q, int? categoryId)
    {
        var register = await BoundRegisterAsync();
        if (register == null) return Json(Array.Empty<object>());
        var storeId = register.StoreId;
        q = (q ?? "").Trim();

        var query = _db.Products.Where(p => p.IsActive);
        if (categoryId.HasValue)
            query = query.Where(p => p.CategoryId == categoryId.Value);
        if (q.Length > 0)
            query = query.Where(p => EF.Functions.ILike(p.Name, $"%{q}%")
                                  || EF.Functions.ILike(p.Sku ?? "", $"%{q}%")
                                  || EF.Functions.ILike(p.Barcode ?? "", $"%{q}%"));

        var products = await query.OrderBy(p => p.Name).Take(40)
            .Select(p => new
            {
                id = p.Id,
                name = p.Name,
                sku = p.Sku,
                barcode = p.Barcode,
                price = p.Price,
                image = p.Images.Where(i => i.IsPrimary).Select(i => i.Url).FirstOrDefault()
                        ?? p.Images.Select(i => i.Url).FirstOrDefault(),
                stock = p.StoreInventories.Where(si => si.StoreId == storeId)
                                          .Select(si => si.QuantityOnHand).FirstOrDefault(),
                variants = p.ProductType == "variable"
                    ? p.Variants.Where(v => v.IsActive)
                        .Select(v => new { id = v.Id, name = v.Name, priceAdjustment = v.PriceAdjustment }).ToList()
                    : null
            })
            .ToListAsync();
        return Json(products);
    }

    public class TillLine
    {
        public int ProductId { get; set; }
        public int? VariantId { get; set; }
        public int Quantity { get; set; }
        public string? Note { get; set; }
        public decimal DiscountAmount { get; set; }
        public string? DiscountReason { get; set; }
        public string? DiscountType { get; set; }
    }
    public class TillCheckout
    {
        public string PaymentMethod { get; set; } = "Cash";
        public decimal AmountTendered { get; set; }
        public List<TillLine> Items { get; set; } = new();
    }
    public class TillCashier { public string Id { get; set; } = ""; public string Name { get; set; } = ""; }

    [Authorize, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Checkout([FromBody] TillCheckout req)
    {
        var register = await BoundRegisterAsync();
        if (register == null) return Json(new { success = false, message = "This till isn't set up. Pick a register." });
        if (req.Items == null || req.Items.Count == 0) return Json(new { success = false, message = "Cart is empty." });

        var session = await OpenSessionAsync(register.Id);
        if (session == null) return Json(new { success = false, message = "Open the till before selling." });

        var storeId = register.StoreId;
        var productIds = req.Items.Select(i => i.ProductId).Distinct().ToList();
        var products = await _db.Products.Include(p => p.Variants)
            .Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

        foreach (var grp in req.Items.GroupBy(i => i.ProductId))
        {
            if (!products.TryGetValue(grp.Key, out var prod))
                return Json(new { success = false, message = "A product in the cart no longer exists." });
            var requested = grp.Sum(i => Math.Max(1, i.Quantity));
            if (requested > await _stock.GetStockAsync(grp.Key, storeId))
                return Json(new { success = false, message = $"Not enough stock for '{prod.Name}'." });
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var now = DateTime.UtcNow;
        var orderNumber = $"POS-{now:yyMMdd}-{now:HHmmssfff}";

        await using var tx = await _db.Database.BeginTransactionAsync();

        var order = new Order
        {
            OrderNumber = orderNumber,
            Channel = OrderChannel.Pos,
            FulfillmentType = FulfillmentType.StorePickup,
            PickupStoreId = storeId,
            RegisterId = register.Id,
            TillSessionId = session.Id,
            UserId = userId,
            Status = OrderStatus.Delivered,
            IsPaid = true,
            PaidAt = now,
            PaymentProvider = req.PaymentMethod,
            CreatedAt = now,
            UpdatedAt = now
        };

        decimal subtotal = 0;
        decimal totalDiscount = 0;
        foreach (var line in req.Items)
        {
            var prod = products[line.ProductId];
            var qty = Math.Max(1, line.Quantity);
            var variant = line.VariantId.HasValue ? prod.Variants.FirstOrDefault(v => v.Id == line.VariantId) : null;
            var unitPrice = prod.Price + (variant?.PriceAdjustment ?? 0);
            var lineDiscount = Math.Max(0, Math.Min(line.DiscountAmount, unitPrice * qty));
            subtotal += unitPrice * qty;
            totalDiscount += lineDiscount;

            order.Items.Add(new OrderItem
            {
                ProductId = prod.Id,
                ProductVariantId = variant?.Id,
                ProductName = prod.Name,
                VariantName = variant?.Name,
                ProductSku = prod.Sku,
                Quantity = qty,
                UnitPrice = unitPrice,
                DiscountAmount = lineDiscount,
                DiscountReason = string.IsNullOrWhiteSpace(line.DiscountReason) ? null : line.DiscountReason.Trim(),
                DiscountType = string.IsNullOrWhiteSpace(line.DiscountType) ? null : line.DiscountType.Trim(),
                ItemNote = string.IsNullOrWhiteSpace(line.Note) ? null : line.Note.Trim()
            });
            await _stock.ApplyAsync(prod.Id, variant?.Id, storeId, -qty, StockMovementType.Sale, orderNumber, userId: userId);
        }

        order.Subtotal = subtotal;
        order.DiscountAmount = totalDiscount;
        order.Total = subtotal - totalDiscount;
        order.AmountTendered = req.AmountTendered > 0 ? req.AmountTendered : order.Total;
        order.ChangeGiven = Math.Max(0, (order.AmountTendered ?? order.Total) - order.Total);

        _db.Orders.Add(order);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        return Json(new { success = true, orderId = order.Id, orderNumber, total = order.Total, change = order.ChangeGiven });
    }

    [Authorize]
    public async Task<IActionResult> Receipt(int id)
    {
        var order = await _db.Orders.Include(o => o.Items).Include(o => o.PickupStore)
            .FirstOrDefaultAsync(o => o.Id == id && o.Channel == OrderChannel.Pos);
        if (order == null) return NotFound();
        return View(order);
    }
}
