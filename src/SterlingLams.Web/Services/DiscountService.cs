using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Models.ViewModels;

namespace SterlingLams.Web.Services;

public class DiscountResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }          // money off the items
    public bool FreeShipping { get; set; }        // waive delivery fee
    public static DiscountResult Fail(string error) => new() { Success = false, Error = error };
}

public interface IDiscountService
{
    /// <summary>Validate and calculate a manually-entered code against the cart + user.</summary>
    Task<DiscountResult> EvaluateAsync(string code, CartViewModel cart, string? userId);

    /// <summary>Find the best automatic (no-code) discount that applies, if any.</summary>
    Task<DiscountResult?> FindAutomaticAsync(CartViewModel cart, string? userId);
}

public class DiscountService : IDiscountService
{
    private readonly ApplicationDbContext _db;

    public DiscountService(ApplicationDbContext db) => _db = db;

    public async Task<DiscountResult> EvaluateAsync(string code, CartViewModel cart, string? userId)
    {
        if (string.IsNullOrWhiteSpace(code))
            return DiscountResult.Fail("Please enter a discount code.");

        var normalized = code.Trim().ToUpper();
        var discount = await _db.DiscountCodes
            .Include(d => d.Categories)
            .Include(d => d.Products)
            .FirstOrDefaultAsync(d => d.Code == normalized);

        if (discount == null)
            return DiscountResult.Fail("Invalid discount code.");
        if (discount.IsAutomatic)
            return DiscountResult.Fail("This is an automatic promotion — no code needed.");

        return await EvaluateDiscountAsync(discount, cart, userId);
    }

    public async Task<DiscountResult?> FindAutomaticAsync(CartViewModel cart, string? userId)
    {
        var autos = await _db.DiscountCodes
            .Include(d => d.Categories)
            .Include(d => d.Products)
            .Where(d => d.IsAutomatic && d.IsActive)
            .ToListAsync();

        DiscountResult? best = null;
        foreach (var d in autos)
        {
            var r = await EvaluateDiscountAsync(d, cart, userId);
            if (r.Success && (best == null || r.Amount > best.Amount || (r.FreeShipping && !best.FreeShipping)))
                best = r;
        }
        return best;
    }

    // ── Core evaluation ─────────────────────────────────────────────────────────
    private async Task<DiscountResult> EvaluateDiscountAsync(DiscountCode d, CartViewModel cart, string? userId)
    {
        var now = DateTime.UtcNow;

        if (!d.IsActive)                                   return DiscountResult.Fail("This discount is not active.");
        if (d.IsScheduled)                                 return DiscountResult.Fail("This discount hasn't started yet.");
        if (d.IsExpired)                                   return DiscountResult.Fail("This discount has expired.");
        if (d.MaxUses.HasValue && d.UsedCount >= d.MaxUses) return DiscountResult.Fail("This discount has reached its usage limit.");

        if (d.MinimumOrderAmount.HasValue && cart.Subtotal < d.MinimumOrderAmount.Value)
            return DiscountResult.Fail($"Spend at least ₦{d.MinimumOrderAmount.Value:N0} to use this discount.");

        if (d.MinimumQuantity.HasValue && cart.TotalItems < d.MinimumQuantity.Value)
            return DiscountResult.Fail($"Add at least {d.MinimumQuantity.Value} item(s) to use this discount.");

        // First-order-only + per-customer limit need the signed-in user
        if (d.FirstOrderOnly)
        {
            if (string.IsNullOrEmpty(userId))
                return DiscountResult.Fail("Sign in to use this first-order discount.");
            var hasOrdered = await _db.Orders.AnyAsync(o => o.UserId == userId && o.IsPaid);
            if (hasOrdered) return DiscountResult.Fail("This discount is for your first order only.");
        }

        if (d.MaxUsesPerCustomer.HasValue)
        {
            if (string.IsNullOrEmpty(userId))
                return DiscountResult.Fail("Sign in to use this discount.");
            var usedByCustomer = await _db.Orders
                .CountAsync(o => o.UserId == userId && o.DiscountCode == d.Code);
            if (usedByCustomer >= d.MaxUsesPerCustomer.Value)
                return DiscountResult.Fail("You've already used this discount the maximum number of times.");
        }

        // Determine the eligible amount based on scope
        var eligible = await EligibleSubtotalAsync(d, cart);
        if (eligible <= 0 && d.Type != DiscountType.FreeShipping)
            return DiscountResult.Fail("No items in your cart qualify for this discount.");

        var result = new DiscountResult
        {
            Success = true,
            Code = d.Code,
            Description = d.Description ?? d.Code,
        };

        switch (d.Type)
        {
            case DiscountType.Percentage:
                result.Amount = Math.Round(eligible * d.Value / 100m, 2);
                break;
            case DiscountType.FixedAmount:
                result.Amount = Math.Min(d.Value, eligible);
                break;
            case DiscountType.FreeShipping:
                result.FreeShipping = true;
                result.Amount = 0;
                break;
        }

        return result;
    }

    /// <summary>Subtotal of the cart items the discount applies to, based on its scope.</summary>
    private async Task<decimal> EligibleSubtotalAsync(DiscountCode d, CartViewModel cart)
    {
        if (d.Scope == DiscountScope.EntireOrder)
            return cart.Subtotal;

        var productIds = cart.Items.Select(i => i.ProductId).Distinct().ToList();

        if (d.Scope == DiscountScope.Products)
        {
            var allowed = d.Products.Select(p => p.ProductId).ToHashSet();
            return cart.Items.Where(i => allowed.Contains(i.ProductId)).Sum(i => i.LineTotal);
        }

        // Categories scope — map cart products to their category
        var allowedCats = d.Categories.Select(c => c.CategoryId).ToHashSet();
        var prodToCat = await _db.Products
            .Where(p => productIds.Contains(p.Id))
            .Select(p => new { p.Id, p.CategoryId })
            .ToDictionaryAsync(p => p.Id, p => p.CategoryId);

        return cart.Items
            .Where(i => prodToCat.TryGetValue(i.ProductId, out var catId) && allowedCats.Contains(catId))
            .Sum(i => i.LineTotal);
    }
}
