using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.Controllers;

/// <summary>Public Journal (blog / lookbook): index of published posts + individual articles.</summary>
[AllowAnonymous]
public class JournalController : Controller
{
    private const int PageSize = 9;
    private readonly ApplicationDbContext _db;
    public JournalController(ApplicationDbContext db) => _db = db;

    [HttpGet("/journal")]
    public async Task<IActionResult> Index(int page = 1)
    {
        if (page < 1) page = 1;
        ViewData["Title"] = "Journal";
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

    [HttpGet("/journal/{slug}")]
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
