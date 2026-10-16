using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

/// <summary>
/// Resolves the thumbnail shown for each order line in an email. Prefers the chosen VARIANT's own picture
/// when it has one (so a "Gold" line shows the gold photo, not the product's default/silver image — which
/// otherwise makes a packer grab the wrong colour), falling back to the product's primary image. URLs are
/// made absolute for email clients. Shared by every order email so they can't disagree.
/// </summary>
public static class OrderEmailImages
{
    /// <summary>Builds a per-item image lookup for the given order items. One pair of queries, then a pure
    /// function: variant image if set, else the product's primary image, both made absolute.</summary>
    public static async Task<Func<OrderItem, string?>> ResolverAsync(
        ApplicationDbContext db, IEnumerable<OrderItem> items, string? baseUrl)
    {
        var list = items.ToList();
        var pids = list.Select(i => i.ProductId).Distinct().ToList();
        var vids = list.Where(i => i.ProductVariantId != null).Select(i => i.ProductVariantId!.Value).Distinct().ToList();

        var prodImg = await db.ProductImages.Where(im => pids.Contains(im.ProductId))
            .GroupBy(im => im.ProductId)
            .Select(g => new { Pid = g.Key, Url = g.OrderByDescending(x => x.IsPrimary).Select(x => x.Url).FirstOrDefault() })
            .ToDictionaryAsync(x => x.Pid, x => x.Url);

        var varImg = vids.Count == 0
            ? new Dictionary<int, string>()
            : await db.ProductVariants.Where(v => vids.Contains(v.Id) && v.ImageUrl != null && v.ImageUrl != "")
                .ToDictionaryAsync(v => v.Id, v => v.ImageUrl!);

        var b = (baseUrl ?? "").TrimEnd('/');
        string? Abs(string? u) => string.IsNullOrWhiteSpace(u) ? null
            : (u.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? u
               : (string.IsNullOrEmpty(b) ? null : b + "/" + u.TrimStart('/')));

        return (OrderItem i) =>
        {
            if (i.ProductVariantId is int vid && varImg.TryGetValue(vid, out var vu))
                return Abs(vu);
            return Abs(prodImg.GetValueOrDefault(i.ProductId));
        };
    }
}
