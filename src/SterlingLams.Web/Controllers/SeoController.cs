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
        var entries = new List<(string Loc, DateTime? Last, string Freq, string Priority)>
        {
            ($"{b}/",               null, "daily",   "1.0"),
            ($"{b}/products",       null, "daily",   "0.9"),
            ($"{b}/Stores",         null, "monthly", "0.5"),
            ($"{b}/Home/About",     null, "monthly", "0.4"),
            ($"{b}/Home/Contact",   null, "monthly", "0.4"),
        };

        var cats = await _db.Categories
            .Where(c => c.IsActive && c.Products.Any(p => p.IsActive))
            .Select(c => c.Slug).ToListAsync();
        foreach (var slug in cats)
            entries.Add(($"{b}/products?category={slug}", null, "weekly", "0.7"));

        var products = await _db.Products
            .Where(p => p.IsActive)
            .Select(p => new { p.Slug, p.UpdatedAt }).ToListAsync();
        foreach (var p in products)
            entries.Add(($"{b}/products/{p.Slug}", p.UpdatedAt, "weekly", "0.6"));

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">");
        foreach (var (loc, last, freq, prio) in entries)
        {
            sb.AppendLine("  <url>");
            sb.AppendLine($"    <loc>{SecurityElement.Escape(loc)}</loc>");
            if (last.HasValue) sb.AppendLine($"    <lastmod>{last.Value:yyyy-MM-dd}</lastmod>");
            sb.AppendLine($"    <changefreq>{freq}</changefreq>");
            sb.AppendLine($"    <priority>{prio}</priority>");
            sb.AppendLine("  </url>");
        }
        sb.AppendLine("</urlset>");
        return Content(sb.ToString(), "application/xml", Encoding.UTF8);
    }
}
