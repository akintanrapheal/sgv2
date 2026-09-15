using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Records each internal API/AJAX request into <see cref="ApiActivityTracker"/>, attributed to a store:
/// POS calls → the till's branch (via the till_register cookie + a cached register→store map);
/// storefront/other internal calls → "Online". Cheap: a path check per request, and one DB lookup at
/// most every couple of minutes for the register map.
/// </summary>
public class ApiActivityMiddleware
{
    private readonly RequestDelegate _next;
    private const string RegisterCookie = "till_register";
    private const string MapCacheKey = "api_activity_register_store_map";

    public ApiActivityMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx, ApiActivityTracker tracker, IMemoryCache cache, ApplicationDbContext db)
    {
        var path = ctx.Request.Path.Value ?? string.Empty;
        if (IsInternalApi(path))
        {
            var label = "Online";
            if (path.StartsWith("/Pos", StringComparison.OrdinalIgnoreCase))
            {
                label = "POS";
                if (int.TryParse(ctx.Request.Cookies[RegisterCookie], out var rid))
                {
                    var map = await RegisterStoreMapAsync(cache, db);
                    if (map.TryGetValue(rid, out var storeName)) label = storeName;
                }
            }
            tracker.Record(label);
        }
        await _next(ctx);
    }

    private static async Task<Dictionary<int, string>> RegisterStoreMapAsync(IMemoryCache cache, ApplicationDbContext db)
    {
        if (cache.TryGetValue(MapCacheKey, out Dictionary<int, string>? m) && m != null) return m;
        m = await db.Registers.AsNoTracking()
            .Select(r => new { r.Id, Name = r.Store.Name })
            .ToDictionaryAsync(x => x.Id, x => x.Name);
        cache.Set(MapCacheKey, m, TimeSpan.FromMinutes(2));
        return m;
    }

    private static bool IsInternalApi(string p)
    {
        bool S(string x) => p.StartsWith(x, StringComparison.OrdinalIgnoreCase);
        return S("/api/") || S("/Cart/") || S("/Pos/") || S("/Wishlist/") || S("/site/")
            || S("/Checkout/FulfilmentPreview") || S("/Home/Subscribe") || S("/Home/WelcomeOffer")
            || S("/Products/NotifyRestock");
    }
}
