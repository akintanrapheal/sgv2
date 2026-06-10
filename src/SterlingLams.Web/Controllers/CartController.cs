using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Models.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace SterlingLams.Web.Controllers;

public class CartController : Controller
{
    private const string CartSessionKey = "cart";
    private readonly ApplicationDbContext _db;
    private readonly IDiscountService _discounts;
    private readonly UserManager<ApplicationUser> _userManager;

    public CartController(ApplicationDbContext db, IDiscountService discounts,
        UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _discounts = discounts;
        _userManager = userManager;
    }

    public async Task<IActionResult> Index()
    {
        var cart = GetCart();
        await ApplyAutomaticDiscountAsync(cart);
        return View(cart);
    }

    [HttpPost]
    public async Task<IActionResult> Add(int productId, int quantity = 1, int? variantId = null)
    {
        var product = await _db.Products
            .Include(p => p.Images)
            .Include(p => p.Variants)
            .FirstOrDefaultAsync(p => p.Id == productId && p.IsActive);

        if (product == null)
            return Json(new { success = false, message = "Product not found." });

        // Combined stock across all active branches is the ceiling (online fulfilment can
        // pull from any branch). StoreInventory is per-product, same as the POS ledger.
        var available = await CombinedAvailableAsync(productId);
        if (available <= 0)
            return Json(new { success = false, message = "This item is out of stock." });

        var cart = GetCart();
        var existing = cart.Items.FirstOrDefault(i => i.ProductId == productId && i.VariantId == variantId);

        if (existing != null)
        {
            existing.MaxQuantity = available;
            existing.Quantity = Math.Min(existing.Quantity + quantity, available);
        }
        else
        {
            var variant = variantId.HasValue ? product.Variants.FirstOrDefault(v => v.Id == variantId) : null;
            cart.Items.Add(new CartItemViewModel
            {
                ProductId = product.Id,
                VariantId = variantId,
                ProductName = product.Name,
                VariantName = variant?.Name,
                Slug = product.Slug,
                ImageUrl = product.Images.FirstOrDefault(i => i.IsPrimary)?.Url ?? "/images/placeholder.jpg",
                UnitPrice = product.Price + (variant?.PriceAdjustment ?? 0),
                Quantity = Math.Min(Math.Max(1, quantity), available),
                MaxQuantity = available
            });
        }

        SaveCart(cart);

        return Json(new
        {
            success = true,
            cartCount = cart.TotalItems,
            subtotal = cart.FormattedSubtotal
        });
    }

    [HttpPost]
    public async Task<IActionResult> UpdateQuantity(int productId, int quantity, int? variantId = null)
    {
        var cart = GetCart();
        var item = cart.Items.FirstOrDefault(i => i.ProductId == productId && i.VariantId == variantId);

        if (item != null)
        {
            if (quantity <= 0)
                cart.Items.Remove(item);
            else
            {
                item.MaxQuantity = await CombinedAvailableAsync(productId); // refresh against live stock
                item.Quantity = Math.Min(quantity, Math.Max(1, item.MaxQuantity));
            }
        }

        SaveCart(cart);
        return Json(new { success = true, cartCount = cart.TotalItems, subtotal = cart.FormattedSubtotal });
    }

    /// <summary>Combined AVAILABLE stock (on-hand minus reservations held by unpaid orders) across
    /// all active branches for a product — the orderable ceiling.</summary>
    private async Task<int> CombinedAvailableAsync(int productId) =>
        await _db.StoreInventories
            .Where(si => si.ProductId == productId && si.Store.IsActive)
            .SumAsync(si => (int?)(si.QuantityOnHand - si.QuantityReserved)) ?? 0;

    [HttpPost]
    public IActionResult Remove(int productId, int? variantId = null)
    {
        var cart = GetCart();
        var item = cart.Items.FirstOrDefault(i => i.ProductId == productId && i.VariantId == variantId);
        if (item != null) cart.Items.Remove(item);
        SaveCart(cart);
        return Json(new { success = true, cartCount = cart.TotalItems });
    }

    [HttpPost]
    public async Task<IActionResult> ApplyDiscount(string code)
    {
        var cart = GetCart();
        var userId = _userManager.GetUserId(User);

        var result = await _discounts.EvaluateAsync(code, cart, userId);
        if (!result.Success)
            return Json(new { success = false, message = result.Error });

        cart.AppliedDiscountCode  = result.Code;
        cart.DiscountDescription  = result.Description;
        cart.DiscountAmount       = result.Amount;
        cart.FreeShipping         = result.FreeShipping;
        cart.IsAutomaticDiscount  = false;
        SaveCart(cart);

        return Json(new
        {
            success = true,
            code = cart.AppliedDiscountCode,
            description = cart.DiscountDescription,
            discount = cart.FormattedDiscount,
            freeShipping = cart.FreeShipping,
            total = cart.FormattedTotal
        });
    }

    [HttpPost]
    public IActionResult RemoveDiscount()
    {
        var cart = GetCart();
        cart.AppliedDiscountCode = null;
        cart.DiscountDescription = null;
        cart.DiscountAmount = 0;
        cart.FreeShipping = false;
        cart.IsAutomaticDiscount = false;
        SaveCart(cart);
        return Json(new { success = true, total = cart.FormattedTotal });
    }

    /// <summary>
    /// Applies the best automatic (no-code) promotion if the customer hasn't already
    /// applied a manual code. Re-evaluates each load so it stays correct as the cart changes.
    /// </summary>
    private async Task ApplyAutomaticDiscountAsync(CartViewModel cart)
    {
        if (cart.IsEmpty) return;

        // Don't override a manually-applied code
        if (!string.IsNullOrEmpty(cart.AppliedDiscountCode) && !cart.IsAutomaticDiscount)
            return;

        var userId = _userManager.GetUserId(User);
        var auto = await _discounts.FindAutomaticAsync(cart, userId);

        if (auto != null)
        {
            cart.AppliedDiscountCode = auto.Code;
            cart.DiscountDescription = auto.Description;
            cart.DiscountAmount      = auto.Amount;
            cart.FreeShipping        = auto.FreeShipping;
            cart.IsAutomaticDiscount = true;
            SaveCart(cart);
        }
        else if (cart.IsAutomaticDiscount)
        {
            // A previously auto-applied promo no longer qualifies — clear it
            cart.AppliedDiscountCode = null;
            cart.DiscountDescription = null;
            cart.DiscountAmount = 0;
            cart.FreeShipping = false;
            cart.IsAutomaticDiscount = false;
            SaveCart(cart);
        }
    }

    // Partial for mini-cart in nav dropdown
    public IActionResult MiniCart()
    {
        return PartialView("_MiniCart", GetCart());
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private CartViewModel GetCart()
    {
        var json = HttpContext.Session.GetString(CartSessionKey);
        if (string.IsNullOrEmpty(json)) return new CartViewModel();
        return JsonSerializer.Deserialize<CartViewModel>(json) ?? new CartViewModel();
    }

    private void SaveCart(CartViewModel cart)
    {
        HttpContext.Session.SetString(CartSessionKey, JsonSerializer.Serialize(cart));
    }
}
