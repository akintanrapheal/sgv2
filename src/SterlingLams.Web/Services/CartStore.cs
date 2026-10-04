using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.ViewModels;

namespace SterlingLams.Web.Services;

/// <summary>
/// Persists the shopper's cart in a durable browser cookie instead of volatile server session
/// (session was wiped on every Render redeploy and after a 30-minute idle — carts vanished at
/// checkout).
///
/// IMPORTANT: the cookie stores only a COMPACT snapshot per line — product id, variant id, quantity,
/// unit price and max-quantity — never the product name, image URL or slug. Those display fields are
/// large (a Cloudinary URL alone is ~110 bytes) and a dozen of them overflowed the browser's ~4 KB
/// per-cookie limit, so adding beyond ~10-12 items silently failed. The snapshot is ~25-45 bytes a
/// line, so a cookie now holds 80+ items. The display fields are rehydrated from the database in
/// <see cref="LoadAsync"/>; price and max-quantity stay in the cookie (captured at add-time, exactly
/// as the old session cart did). The nav badge uses <see cref="Count"/>, which needs no database hit.
/// </summary>
public static class CartStore
{
    public const string CookieName = "sg_cart";
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
    private static readonly ChunkingCookieManager Cookies = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // Compact wire format — short property names keep the cookie small.
    private sealed class Line
    {
        public int P { get; set; }          // ProductId
        public int? V { get; set; }         // VariantId
        public int Q { get; set; }          // Quantity
        public decimal U { get; set; }      // UnitPrice (captured at add-time)
        public int M { get; set; } = 10;    // MaxQuantity (stock at add-time)
    }
    private sealed class Snap
    {
        public List<Line> I { get; set; } = new();   // bag
        public List<Line> S { get; set; } = new();   // saved for later
        public string? Dc { get; set; }              // discount code
        public string? Dd { get; set; }              // discount description
        public decimal Da { get; set; }              // discount amount
        public bool Fs { get; set; }                 // free shipping
        public bool Fl { get; set; }                 // free shipping, Lagos/Abuja only
        public bool Au { get; set; }                 // discount is automatic
    }

    // ── Read ────────────────────────────────────────────────────────────────────

    private static Snap? ReadSnap(HttpContext ctx)
    {
        string? json;
        try { json = Cookies.GetRequestCookie(ctx, CookieName); }
        catch { return null; }   // partial/garbled chunks
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<Snap>(json, JsonOpts); }
        catch { return null; }
    }

    /// <summary>Total units in the bag, read straight from the cookie (no database). For the nav badge.</summary>
    public static int Count(HttpContext ctx)
    {
        var snap = ReadSnap(ctx);
        return snap?.I.Sum(l => l.Q) ?? 0;
    }

    /// <summary>Rebuilds the full cart: quantity/price/max from the cookie, display fields (name, image,
    /// slug, variant name) from the database. Lines whose product no longer exists are dropped. Never throws.</summary>
    public static async Task<CartViewModel> LoadAsync(HttpContext ctx, ApplicationDbContext db)
    {
        var snap = ReadSnap(ctx);
        var cart = new CartViewModel();
        if (snap == null) return cart;

        cart.AppliedDiscountCode = snap.Dc;
        cart.DiscountDescription = snap.Dd;
        cart.DiscountAmount = snap.Da;
        cart.FreeShipping = snap.Fs;
        cart.FreeShippingLagosAbujaOnly = snap.Fl;
        cart.IsAutomaticDiscount = snap.Au;

        var ids = snap.I.Select(l => l.P).Concat(snap.S.Select(l => l.P)).Distinct().ToList();
        if (ids.Count == 0) return cart;

        var products = await db.Products
            .Where(p => ids.Contains(p.Id))
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.Slug,
                Image = p.Images.OrderByDescending(i => i.IsPrimary).ThenBy(i => i.SortOrder)
                    .Select(i => i.Url).FirstOrDefault(),
                Variants = p.Variants.Select(v => new { v.Id, v.Name }).ToList(),
            })
            .ToListAsync();
        var byId = products.ToDictionary(p => p.Id);

        CartItemViewModel? Build(Line l)
        {
            if (!byId.TryGetValue(l.P, out var p)) return null;   // product deleted → drop the line
            var variantName = l.V is int vid ? p.Variants.FirstOrDefault(v => v.Id == vid)?.Name : null;
            return new CartItemViewModel
            {
                ProductId = l.P,
                VariantId = l.V,
                Quantity = l.Q,
                UnitPrice = l.U,
                MaxQuantity = l.M,
                ProductName = p.Name,
                VariantName = variantName,
                Slug = p.Slug,
                ImageUrl = p.Image ?? "/images/placeholder.jpg",
            };
        }

        cart.Items = snap.I.Select(Build).Where(i => i != null).Select(i => i!).ToList();
        cart.SavedItems = snap.S.Select(Build).Where(i => i != null).Select(i => i!).ToList();
        return cart;
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    /// <summary>Writes the cart back to a persistent cookie as a compact snapshot. An empty cart (no bag
    /// and no saved items) deletes the cookie instead of storing an empty shell.</summary>
    public static void Save(HttpContext ctx, CartViewModel cart)
    {
        if (cart.IsEmpty && (cart.SavedItems is null || cart.SavedItems.Count == 0))
        {
            Clear(ctx);
            return;
        }

        static Line ToLine(CartItemViewModel i) => new()
        {
            P = i.ProductId, V = i.VariantId, Q = i.Quantity, U = i.UnitPrice, M = i.MaxQuantity,
        };

        var snap = new Snap
        {
            I = cart.Items.Select(ToLine).ToList(),
            S = (cart.SavedItems ?? new()).Select(ToLine).ToList(),
            Dc = cart.AppliedDiscountCode,
            Dd = cart.DiscountDescription,
            Da = cart.DiscountAmount,
            Fs = cart.FreeShipping,
            Fl = cart.FreeShippingLagosAbujaOnly,
            Au = cart.IsAutomaticDiscount,
        };

        var json = JsonSerializer.Serialize(snap, JsonOpts);
        Cookies.AppendResponseCookie(ctx, CookieName, json, BuildOptions(ctx, persistent: true));
    }

    /// <summary>Removes the cart cookie (e.g. after a completed order).</summary>
    public static void Clear(HttpContext ctx) =>
        Cookies.DeleteCookie(ctx, CookieName, BuildOptions(ctx, persistent: false));

    private static CookieOptions BuildOptions(HttpContext ctx, bool persistent)
    {
        var opts = new CookieOptions
        {
            HttpOnly = true,                 // cart is never read by client JS (the badge comes from /site/header-state)
            IsEssential = true,              // functional cookie — not gated by cookie-consent
            SameSite = SameSiteMode.Lax,     // sent on top-level navigations (incl. return from Paystack)
            Secure = ctx.Request.IsHttps,    // Secure in prod (HTTPS), off in local dev (HTTP)
            Path = "/",
        };
        if (persistent) opts.Expires = DateTimeOffset.UtcNow.Add(Lifetime);
        return opts;
    }
}
