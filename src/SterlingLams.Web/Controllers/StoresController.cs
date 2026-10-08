using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.Controllers;

public class StoresController : Controller
{
    private readonly ApplicationDbContext _db;

    public StoresController(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<IActionResult> Index()
    {
        // Customer-facing: only branches that are active AND public (hide branches being stocked up
        // before they open to customers — IsActive but not IsPublic).
        var stores = await _db.Stores.Where(s => s.IsActive && s.IsPublic).ToListAsync();
        return View(stores);
    }
}
