using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.Controllers;

/// <summary>Public Blog (formerly "Journal"): index of published posts + individual articles.
/// Served at /blog; the controller/view names stay "Journal" so routing, permissions and asp-* links
/// are untouched (the rename is user-visible only). Old /journal URLs 301 to /blog via the legacy
/// redirect middleware.</summary>
[AllowAnonymous]
public class JournalController : Controller
{
    private const int PageSize = 9;
    private readonly ApplicationDbContext _db;
    public JournalController(ApplicationDbContext db) => _db = db;

    [HttpGet("/blog")]
    public async Task<IActionResult> Index(int page = 1)
    {
        if (page < 1) page = 1;
        ViewData["Title"] = "Blog";
        ViewData["Description"] = "Styling notes, lookbooks and stories from Sterlin Glams.";

        var published = _db.BlogPosts.AsNoTracking().Where(b => b.IsPublished);
        var total = await published.CountAsync();
        var posts = await published
            .OrderByDescending(b => b.PublishedAt)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .ToListAsync();

        ViewBag.Page = page;
        ViewBag.TotalPages = (int)System.Math.Ceiling(total / (double)PageSize);
        return View(posts);
    }

    [HttpGet("/blog/{slug}")]
    public async Task<IActionResult> Detail(string slug)
    {
        var post = await _db.BlogPosts.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Slug == slug && b.IsPublished);
        if (post == null) return NotFound();

        // A few more recent posts to keep readers moving.
        ViewBag.More = await _db.BlogPosts.AsNoTracking()
            .Where(b => b.IsPublished && b.Id != post.Id)
            .OrderByDescending(b => b.PublishedAt)
            .Take(3)
            .ToListAsync();

        return View(post);
    }
}
