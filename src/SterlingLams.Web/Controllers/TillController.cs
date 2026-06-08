using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Controllers;

/// <summary>
/// The dedicated full-screen till / register app. Runs on the counter device: bind it to a
/// register (branch) once, then cashiers sign in with a PIN and ring up sales. Shares the same
/// products, stock ledger and orders as the rest of the system.
/// </summary>
[AllowAnonymous]
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

        ViewData["Session"] = session;
        return View("Sell", register);
    }

    [Authorize, HttpPost]
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

    [Authorize, HttpPost]
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

    [Authorize, HttpPost]
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

    [HttpPost]
    public IActionResult SetRegister(int registerId)
    {
        Response.Cookies.Append(RegisterCookie, registerId.ToString(),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), HttpOnly = true, IsEssential = true });
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public IActionResult ChangeRegister()
    {
        Response.Cookies.Delete(RegisterCookie);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
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

    [Authorize, HttpPost]
    public async Task<IActionResult> Logout()
    {
        await _signIn.SignOutAsync();
        return RedirectToAction(nameof(Index));
    }

    // ── Product search for the bound register's store ─────────────────────────
    [Authorize, HttpGet]
    public async Task<IActionResult> Search(string? q)
    {
        var register = await BoundRegisterAsync();
        if (register == null) return Json(Array.Empty<object>());
        var storeId = register.StoreId;
        q = (q ?? "").Trim();

        var query = _db.Products.Where(p => p.IsActive);
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

    public class TillLine { public int ProductId { get; set; } public int? VariantId { get; set; } public int Quantity { get; set; } }
    public class TillCheckout
    {
        public string PaymentMethod { get; set; } = "Cash";
        public decimal AmountTendered { get; set; }
        public List<TillLine> Items { get; set; } = new();
    }

    [Authorize, HttpPost]
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
        foreach (var line in req.Items)
        {
            var prod = products[line.ProductId];
            var qty = Math.Max(1, line.Quantity);
            var variant = line.VariantId.HasValue ? prod.Variants.FirstOrDefault(v => v.Id == line.VariantId) : null;
            var unitPrice = prod.Price + (variant?.PriceAdjustment ?? 0);
            subtotal += unitPrice * qty;

            order.Items.Add(new OrderItem
            {
                ProductId = prod.Id,
                ProductVariantId = variant?.Id,
                ProductName = prod.Name,
                VariantName = variant?.Name,
                Quantity = qty,
                UnitPrice = unitPrice
            });
            await _stock.ApplyAsync(prod.Id, variant?.Id, storeId, -qty, StockMovementType.Sale, orderNumber, userId: userId);
        }

        order.Subtotal = subtotal;
        order.Total = subtotal;
        order.AmountTendered = req.AmountTendered > 0 ? req.AmountTendered : subtotal;
        order.ChangeGiven = Math.Max(0, (order.AmountTendered ?? subtotal) - subtotal);

        _db.Orders.Add(order);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        return Json(new { success = true, orderId = order.Id, orderNumber, total = subtotal, change = order.ChangeGiven });
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

public class TillCashier { public string Id { get; set; } = ""; public string Name { get; set; } = ""; }
