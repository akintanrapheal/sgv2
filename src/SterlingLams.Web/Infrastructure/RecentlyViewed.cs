namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Tracks a shopper's recently-viewed products in a small cookie (no DB, works for guests).
/// Stored as a CSV of product ids, most-recent first, capped at <see cref="Max"/>.
/// </summary>
public static class RecentlyViewed
{
    private const string CookieKey = "rv";
    private const int Max = 8;

    public static List<int> Get(HttpRequest request)
    {
        var raw = request.Cookies[CookieKey];
        if (string.IsNullOrEmpty(raw)) return new();
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .Take(Max)
            .ToList();
    }

    public static void Record(HttpRequest request, HttpResponse response, int productId)
    {
        var ids = Get(request);
        ids.RemoveAll(id => id == productId);
        ids.Insert(0, productId);
        if (ids.Count > Max) ids = ids.Take(Max).ToList();

        response.Cookies.Append(CookieKey, string.Join(',', ids), new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromDays(30),
        });
    }
}
