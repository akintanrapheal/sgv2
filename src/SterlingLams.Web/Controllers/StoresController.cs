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
        var stores = await _db.Stores.Where(s => s.IsActive).ToListAsync();
        return View(stores);
    }
}
