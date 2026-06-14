using System.Security;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.Controllers;

/// <summary>Serves /robots.txt and a dynamic /sitemap.xml for search engines.</summary>
public class SeoController : Controller
{
    private readonly ApplicationDbContext _db;
    public SeoController(ApplicationDbContext db) => _db = db;

    private string BaseUrl => $"{Request.Scheme}://{Request.Host}";

    [HttpGet("/robots.txt")]
    public IActionResult Robots()
    {
        var sb = new StringBuilder();
        sb.AppendLine("User-agent: *");
        foreach (var path in new[] { "/Admin", "/Till", "/Checkout", "/Cart", "/Account", "/Wishlist", "/api/" })
            sb.AppendLine($"Disallow: {path}");
        sb.AppendLine();
        sb.AppendLine($"Sitemap: {BaseUrl}/sitemap.xml");
        return Content(sb.ToString(), "text/plain", Encoding.UTF8);
    }

    [HttpGet("/sitemap.xml")]
    public async Task<IActionResult> Sitemap()
    {
        var b = BaseUrl;
        // ImageLoc: absolute URL of the product's primary image (null = no image entry).
        var entries = new List<(string Loc, DateTime? Last, string Freq, string Priority, string? ImageLoc)>
        {
            ($"{b}/",               null, "daily",   "1.0", null),
            ($"{b}/products",       null, "daily",   "0.9", null),
            ($"{b}/Stores",         null, "monthly", "0.5", null),
            ($"{b}/Home/About",     null, "monthly", "0.4", null),
            ($"{b}/Home/Contact",   null, "monthly", "0.4", null),
        };

        var cats = await _db.Categories
            .Where(c => c.IsActive && c.Products.Any(p => p.IsActive))
            .Select(c => c.Slug).ToListAsync();
        foreach (var slug in cats)
            entries.Add(($"{b}/products?category={slug}", null, "weekly", "0.7", null));

        var products = await _db.Products
            .Where(p => p.IsActive)
            .Select(p => new
            {
                p.Slug,
                p.UpdatedAt,
                Image = p.Images.OrderByDescending(i => i.IsPrimary).ThenBy(i => i.SortOrder)
                    .Select(i => i.Url).FirstOrDefault()
            }).ToListAsync();
        foreach (var p in products)
        {
            // Make image URLs absolute (sitemap image:loc must be fully-qualified).
            string? img = string.IsNullOrEmpty(p.Image) ? null : (p.Image.StartsWith("http") ? p.Image : b + p.Image);
            entries.Add(($"{b}/products/{p.Slug}", p.UpdatedAt, "weekly", "0.6", img));
        }

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\" xmlns:image=\"http://www.google.com/schemas/sitemap-image/1.1\">");
        foreach (var (loc, last, freq, prio, imageLoc) in entries)
        {
            sb.AppendLine("  <url>");
            sb.AppendLine($"    <loc>{SecurityElement.Escape(loc)}</loc>");
            if (last.HasValue) sb.AppendLine($"    <lastmod>{last.Value:yyyy-MM-dd}</lastmod>");
            sb.AppendLine($"    <changefreq>{freq}</changefreq>");
            sb.AppendLine($"    <priority>{prio}</priority>");
            if (!string.IsNullOrEmpty(imageLoc))
            {
                sb.AppendLine("    <image:image>");
                sb.AppendLine($"      <image:loc>{SecurityElement.Escape(imageLoc)}</image:loc>");
                sb.AppendLine("    </image:image>");
            }
            sb.AppendLine("  </url>");
        }
        sb.AppendLine("</urlset>");
        return Content(sb.ToString(), "application/xml", Encoding.UTF8);
    }
}
