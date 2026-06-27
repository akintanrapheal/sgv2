using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.ViewModels;

namespace SterlingLams.Web.Controllers;

/// <summary>
/// Per-user "header state" served fresh on every page load. The storefront's big read-only
/// pages (home / category / product) are output-cached, so their HTML can't carry per-user
/// data — the nav badges, signed-in state, and a valid antiforgery token all come from here
/// instead. This endpoint is explicitly never cached and also sets the antiforgery cookie.
/// </summary>
[AllowAnonymous]
public class SiteController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IAntiforgery _antiforgery;

    public SiteController(ApplicationDbContext db, IAntiforgery antiforgery)
    {
        _db = db;
        _antiforgery = antiforgery;
    }

    [HttpGet("/site/header-state")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> HeaderState()
    {
        // Cart count from the session cart (same key + shape CartController uses).
        var cartCount = 0;
        var cartJson = HttpContext.Session.GetString("cart");
        if (!string.IsNullOrEmpty(cartJson))
        {
            try { cartCount = JsonSerializer.Deserialize<CartViewModel>(cartJson)?.TotalItems ?? 0; }
            catch { /* malformed cart — treat as empty */ }
        }

        var authed = User.Identity?.IsAuthenticated == true;
        var wishlistCount = 0;
        int[] wishlistProductIds = System.Array.Empty<int>();
        if (authed)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
            if (!string.IsNullOrEmpty(userId))
            {
                // The product cards on output-cached pages render with empty hearts; the client
                // fills these in so the signed-in shopper still sees what they've saved.
                wishlistProductIds = await _db.WishlistItems
                    .Where(w => w.UserId == userId)
                    .Select(w => w.ProductId)
                    .ToArrayAsync();
                wishlistCount = wishlistProductIds.Length;
            }
        }

        // Issues + stores the antiforgery cookie and returns a matching request token, so POSTs
        // (add-to-cart, wishlist toggle) from an output-cached page still pass CSRF validation.
        var tokens = _antiforgery.GetAndStoreTokens(HttpContext);

        // Defence in depth: make sure no proxy/browser caches a per-user payload.
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";

        return Json(new
        {
            authenticated = authed,
            cartCount,
            wishlistCount,
            wishlistProductIds,
            antiforgeryToken = tokens.RequestToken
        });
    }
}
