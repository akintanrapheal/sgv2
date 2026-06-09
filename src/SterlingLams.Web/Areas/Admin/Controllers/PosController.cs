using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers;

public class PosController : AdminBaseController
{
    protected override string Section => "POS";

    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;

    public PosController(ApplicationDbContext db, IStockService stock)
    {
        _db = db;
        _stock = stock;
    }

    // ── POS register screen ───────────────────────────────────────────────────
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Point of Sale";
        var stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        return View(stores);
    }

    // ── Product search (JSON) — only products with stock at the chosen store ──
    [HttpGet]
    public async Task<IActionResult> Search(string? q, int storeId)
    {
        q = (q ?? "").Trim();
        var query = _db.Products.Where(p => p.IsActive);
        if (q.Length > 0)
            query = query.Where(p => EF.Functions.ILike(p.Name, $"%{q}%")
                                  || EF.Functions.ILike(p.Sku ?? "", $"%{q}%"));

        var products = await query
            .OrderBy(p => p.Name)
            .Take(40)
            .Select(p => new
            {
                id = p.Id,
                name = p.Name,
                sku = p.Sku,
                price = p.Price,
                image = p.Images.Where(i => i.IsPrimary).Select(i => i.Url).FirstOrDefault()
                        ?? p.Images.Select(i => i.Url).FirstOrDefault(),
                stock = p.StoreInventories.Where(si => si.StoreId == storeId)
                                          .Select(si => si.QuantityOnHand).FirstOrDefault(),
                variants = p.ProductType == "variable"
                    ? p.Variants.Where(v => v.IsActive)
                        .Select(v => new { id = v.Id, name = v.Name, priceAdjustment = v.PriceAdjustment })
                        .ToList()
                    : null
            })
            .ToListAsync();

        return Json(products);
    }

    public class PosLine
    {
        public int ProductId { get; set; }
        public int? VariantId { get; set; }
        public int Quantity { get; set; }
    }

    public class PosCheckoutRequest
    {
        public int StoreId { get; set; }
        public string PaymentMethod { get; set; } = "Cash";
        public decimal AmountTendered { get; set; }
        public string? CustomerNote { get; set; }
        public List<PosLine> Items { get; set; } = new();
    }

    // ── Complete a sale ───────────────────────────────────────────────────────
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Checkout([FromBody] PosCheckoutRequest req)
    {
        if (req.Items == null || req.Items.Count == 0)
            return Json(new { success = false, message = "Cart is empty." });

        var store = await _db.Stores.FirstOrDefaultAsync(s => s.Id == req.StoreId && s.IsActive);
        if (store == null)
            return Json(new { success = false, message = "Please choose a valid branch." });

        // Load the products referenced, with their variants, and price server-side (don't trust client).
        var productIds = req.Items.Select(i => i.ProductId).Distinct().ToList();
        var products = await _db.Products
            .Include(p => p.Variants)
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id);

        // Validate stock per product (summing duplicate lines).
        foreach (var grp in req.Items.GroupBy(i => i.ProductId))
        {
            if (!products.TryGetValue(grp.Key, out var prod))
                return Json(new { success = false, message = "A product in the cart no longer exists." });
            var requested = grp.Sum(i => Math.Max(1, i.Quantity));
            var available = await _stock.GetStockAsync(grp.Key, req.StoreId);
            if (requested > available)
                return Json(new { success = false, message = $"Not enough stock for '{prod.Name}' at {store.Name} ({available} left)." });
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
            PickupStoreId = store.Id,
            UserId = userId,
            Status = OrderStatus.Delivered,
            IsPaid = true,
            PaidAt = now,
            PaymentProvider = req.PaymentMethod,
            Notes = string.IsNullOrWhiteSpace(req.CustomerNote) ? null : req.CustomerNote.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        };

        decimal subtotal = 0;
        foreach (var line in req.Items)
        {
            var prod = products[line.ProductId];
            var qty = Math.Max(1, line.Quantity);
            ProductVariant? variant = line.VariantId.HasValue
                ? prod.Variants.FirstOrDefault(v => v.Id == line.VariantId)
                : null;
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

            await _stock.ApplyAsync(prod.Id, variant?.Id, store.Id, -qty,
                StockMovementType.Sale, orderNumber, userId: userId);
        }

        order.Subtotal = subtotal;
        order.Total = subtotal;
        order.AmountTendered = req.AmountTendered > 0 ? req.AmountTendered : subtotal;
        order.ChangeGiven = Math.Max(0, (order.AmountTendered ?? subtotal) - subtotal);

        _db.Orders.Add(order);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        await LogAsync("Create", "POS Sale", order.Id.ToString(),
            $"POS sale {orderNumber} at {store.Name} — ₦{subtotal:N0} ({order.Items.Count} item(s))");

        return Json(new
        {
            success = true,
            orderId = order.Id,
            orderNumber,
            total = subtotal,
            change = order.ChangeGiven
        });
    }

    // ── Discount Reasons config ───────────────────────────────────────────────
    public async Task<IActionResult> DiscountReasons()
    {
        ViewData["Title"] = "POS Discount Reasons";
        var reasons = await _db.PosDiscountReasons
            .Include(r => r.Presets.OrderBy(p => p.SortOrder))
            .OrderBy(r => r.SortOrder)
            .ToListAsync();
        return View(reasons);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateReason(string name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var maxSort = await _db.PosDiscountReasons.MaxAsync(r => (int?)r.SortOrder) ?? 0;
            _db.PosDiscountReasons.Add(new PosDiscountReason { Name = name.Trim(), SortOrder = maxSort + 10, IsActive = true });
            await _db.SaveChangesAsync();
        }
        return RedirectToAction(nameof(DiscountReasons));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> EditReason(int id, string name, bool isActive)
    {
        var reason = await _db.PosDiscountReasons.FindAsync(id);
        if (reason != null && !string.IsNullOrWhiteSpace(name))
        {
            reason.Name = name.Trim();
            reason.IsActive = isActive;
            await _db.SaveChangesAsync();
        }
        return RedirectToAction(nameof(DiscountReasons));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteReason(int id)
    {
        var reason = await _db.PosDiscountReasons.FindAsync(id);
        if (reason != null) { _db.PosDiscountReasons.Remove(reason); await _db.SaveChangesAsync(); }
        return RedirectToAction(nameof(DiscountReasons));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreatePreset(int reasonId, string label, string type, decimal value)
    {
        if (!string.IsNullOrWhiteSpace(label) && value > 0)
        {
            var maxSort = await _db.PosDiscountPresets.Where(p => p.ReasonId == reasonId).MaxAsync(p => (int?)p.SortOrder) ?? 0;
            _db.PosDiscountPresets.Add(new PosDiscountPreset
            {
                ReasonId = reasonId, Label = label.Trim(),
                Type = type, Value = value, SortOrder = maxSort + 10
            });
            await _db.SaveChangesAsync();
        }
        return RedirectToAction(nameof(DiscountReasons));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeletePreset(int id)
    {
        var preset = await _db.PosDiscountPresets.FindAsync(id);
        if (preset != null) { _db.PosDiscountPresets.Remove(preset); await _db.SaveChangesAsync(); }
        return RedirectToAction(nameof(DiscountReasons));
    }

    // ── Printable receipt ─────────────────────────────────────────────────────
    public async Task<IActionResult> Receipt(int id)
    {
        var order = await _db.Orders
            .Include(o => o.Items)
            .Include(o => o.PickupStore)
            .FirstOrDefaultAsync(o => o.Id == id && o.Channel == OrderChannel.Pos);
        if (order == null) return NotFound();
        ViewData["Title"] = $"Receipt {order.OrderNumber}";
        return View(order);
    }

    public class SessionRow
    {
        public TillSession Session { get; set; } = null!;
        public int SaleCount { get; set; }
        public decimal Sales { get; set; }
        public decimal ExpectedCash { get; set; }
        public decimal? Variance { get; set; }
    }

    // ── Till sessions oversight (cash-ups / Z-reports) ────────────────────────
    public async Task<IActionResult> Sessions()
    {
        ViewData["Title"] = "Till Sessions";
        var sessions = await _db.TillSessions
            .Include(s => s.Register).ThenInclude(r => r.Store)
            .OrderByDescending(s => s.OpenedAt).Take(100).ToListAsync();
        var ids = sessions.Select(s => s.Id).ToList();

        var sales = await _db.Orders.Where(o => o.TillSessionId != null && ids.Contains(o.TillSessionId!.Value))
            .GroupBy(o => o.TillSessionId!.Value)
            .Select(g => new { Id = g.Key, Total = g.Sum(x => x.Total), Count = g.Count(),
                               Cash = g.Where(x => x.PaymentProvider == "Cash").Sum(x => x.Total) })
            .ToListAsync();
        var refunds = await _db.Refunds.Where(r => r.TillSessionId != null && ids.Contains(r.TillSessionId!.Value))
            .GroupBy(r => r.TillSessionId!.Value)
            .Select(g => new { Id = g.Key, CashRef = g.Where(x => x.RefundMethod == "Cash").Sum(x => x.Amount) })
            .ToListAsync();

        var rows = sessions.Select(s =>
        {
            var sa = sales.FirstOrDefault(x => x.Id == s.Id);
            var rf = refunds.FirstOrDefault(x => x.Id == s.Id);
            var expected = s.OpeningFloat + (sa?.Cash ?? 0) - (rf?.CashRef ?? 0);
            return new SessionRow
            {
                Session = s,
                SaleCount = sa?.Count ?? 0,
                Sales = sa?.Total ?? 0,
                ExpectedCash = expected,
                Variance = s.ClosedAt.HasValue ? (s.CountedCash ?? 0) - expected : (decimal?)null
            };
        }).ToList();

        return View(rows);
    }

    // ── POS sales history ─────────────────────────────────────────────────────
    public async Task<IActionResult> Sales()
    {
        ViewData["Title"] = "POS Sales";
        var sales = await _db.Orders
            .Where(o => o.Channel == OrderChannel.Pos)
            .Include(o => o.PickupStore)
            .Include(o => o.Items)
            .OrderByDescending(o => o.CreatedAt)
            .Take(100)
            .ToListAsync();
        return View(sales);
    }
}
