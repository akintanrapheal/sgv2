using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using SterlingLams.Web.Models.ViewModels;

namespace SterlingLams.Web.Services;

/// <summary>
/// Persists the shopper's cart in a durable, chunked browser cookie instead of volatile server
/// session.
///
/// WHY: the cart used to live in <c>HttpContext.Session</c>, which is backed by an in-memory
/// distributed cache (<c>AddDistributedMemoryCache</c>) with a 30-minute idle timeout. On Render
/// (single instance, auto-deploy on every push) that meant a cart was silently emptied whenever
/// either the app restarted/redeployed OR the shopper browsed for more than 30 minutes — so
/// customers hit an empty cart right as they went to check out. A cookie survives both: it lasts
/// 30 days and is independent of server memory.
///
/// The cookie holds the same <see cref="CartViewModel"/> JSON the session did (minus the computed
/// get-only fields, which are <c>[JsonIgnore]</c>'d so the cookie stays small). ChunkingCookieManager
/// transparently splits larger carts across multiple cookies, so there is no 4 KB ceiling.
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

    /// <summary>Reads the cart from the request cookie. Never throws — a malformed or partial
    /// cookie is treated as an empty cart.</summary>
    public static CartViewModel Load(HttpContext ctx)
    {
        string? json;
        try { json = Cookies.GetRequestCookie(ctx, CookieName); }
        catch { return new CartViewModel(); }   // partial/garbled chunks

        if (string.IsNullOrEmpty(json)) return new CartViewModel();
        try { return JsonSerializer.Deserialize<CartViewModel>(json, JsonOpts) ?? new CartViewModel(); }
        catch { return new CartViewModel(); }
    }

    /// <summary>Writes the cart back to a persistent cookie. An empty cart (no bag items and no
    /// saved-for-later items) deletes the cookie instead of storing an empty shell.</summary>
    public static void Save(HttpContext ctx, CartViewModel cart)
    {
        if (cart.IsEmpty && (cart.SavedItems is null || cart.SavedItems.Count == 0))
        {
            Clear(ctx);
            return;
        }

        var json = JsonSerializer.Serialize(cart, JsonOpts);
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
