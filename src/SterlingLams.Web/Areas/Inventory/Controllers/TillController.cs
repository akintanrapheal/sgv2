using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

public class TillController : InventoryAreaController
{
    private readonly ApplicationDbContext _db;
    public TillController(ApplicationDbContext db) => _db = db;

    // Till oversight: cash-up sessions across all branches + today's POS totals.
    public async Task<IActionResult> Index(int? storeId = null, string status = "all")
    {
        ViewData["Title"] = "POS";
        var today = DateTime.UtcNow.Date;

        ViewBag.Stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        ViewBag.StoreId = storeId;
        ViewBag.Status = status;

        var sessionsQuery = _db.TillSessions
            .Include(s => s.Register).ThenInclude(r => r.Store)
            .AsQueryable();

        if (storeId.HasValue)
            sessionsQuery = sessionsQuery.Where(s => s.Register.StoreId == storeId.Value);

        sessionsQuery = status switch
        {
            "open" => sessionsQuery.Where(s => s.ClosedAt == null),
            "closed" => sessionsQuery.Where(s => s.ClosedAt != null),
            _ => sessionsQuery
        };

        var sessions = await sessionsQuery
            .OrderByDescending(s => s.OpenedAt).Take(100).ToListAsync();
        var ids = sessions.Select(s => s.Id).ToList();

        var sales = await _db.Orders.Where(o => o.TillSessionId != null && ids.Contains(o.TillSessionId!.Value))
            .GroupBy(o => o.TillSessionId!.Value)
            .Select(g => new { Id = g.Key, Total = g.Sum(x => x.Total), Count = g.Count(),
                               Cash = g.Where(x => x.PaymentProvider == "Cash").Sum(x => x.Total) })
            .ToListAsync();
        var refunds = await _db.Refunds.Where(r => r.TillSessionId != null && ids.Contains(r.TillSessionId!.Value))
            .GroupBy(r => r.TillSessionId!.Value)
            .Select(g => new { Id = g.Key, CashRef = g.Where(x => x.RefundMethod == "Cash").Sum(x => x.Amount) })
            .ToListAsync();

        var vm = new TillOversightViewModel
        {
            OpenSessions = sessions.Count(s => s.ClosedAt == null),
            Rows = sessions.Select(s =>
            {
                var sa = sales.FirstOrDefault(x => x.Id == s.Id);
                var rf = refunds.FirstOrDefault(x => x.Id == s.Id);
                var expected = s.OpeningFloat + (sa?.Cash ?? 0) - (rf?.CashRef ?? 0);
                return new TillSessionRow
                {
                    Session = s,
                    SaleCount = sa?.Count ?? 0,
                    Sales = sa?.Total ?? 0,
                    ExpectedCash = expected,
                    Variance = s.ClosedAt.HasValue ? (s.CountedCash ?? 0) - expected : (decimal?)null
                };
            }).ToList()
        };

        var posToday = _db.Orders.Where(o => o.Channel == OrderChannel.Pos && o.CreatedAt >= today);
        vm.SalesToday = await posToday.SumAsync(o => (decimal?)o.Total) ?? 0;
        vm.TxToday = await posToday.CountAsync();

        return View(vm);
    }
}

public class TillOversightViewModel
{
    public int OpenSessions { get; set; }
    public decimal SalesToday { get; set; }
    public int TxToday { get; set; }
    public List<TillSessionRow> Rows { get; set; } = new();
}
public class TillSessionRow
{
    public TillSession Session { get; set; } = null!;
    public int SaleCount { get; set; }
    public decimal Sales { get; set; }
    public decimal ExpectedCash { get; set; }
    public decimal? Variance { get; set; }
}
