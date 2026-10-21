using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.Controllers;

public class StoresController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly SterlingLams.Web.Services.IMerchandisingService _merch;

    public StoresController(ApplicationDbContext db, SterlingLams.Web.Services.IMerchandisingService merch)
    {
        _db = db;
        _merch = merch;
    }

    public async Task<IActionResult> Index()
    {
        // Customer-facing: only branches that are active AND public (hide branches being stocked up
        // before they open to customers — IsActive but not IsPublic).
        var stores = await _db.Stores.Where(s => s.IsActive && s.IsPublic).ToListAsync();
        return View(stores);
    }

    // GET /stores/{slug} — a dedicated landing page per physical store. Each has its own URL, title,
    // content and LocalBusiness schema, which is what Google ranks for "jewellery store <city>" and the
    // local map pack (far stronger than one combined /stores page).
    [HttpGet("stores/{slug}")]
    public async Task<IActionResult> Details(string slug)
    {
        var store = await _db.Stores.FirstOrDefaultAsync(s => s.Slug == slug && s.IsActive && s.IsPublic);
        if (store == null) return NotFound();

        // "Shop our collection" strip — reuse the merchandising cards (fully built: price, image, rating).
        ViewBag.FeaturedProducts = await _merch.NewArrivalsAsync(8);
        return View(store);
    }
}
