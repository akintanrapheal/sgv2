using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers;

public class PosController : AdminBaseController
{
    protected override string Section => "POS";

    private readonly ApplicationDbContext _db;

    public PosController(ApplicationDbContext db)
    {
        _db = db;
    }

    // ── Till hub: land on Sessions (cashiers ring up sales in the dedicated /till app) ──
    public IActionResult Index() => RedirectToAction(nameof(Sessions));

    // ── Discount Reasons config ───────────────────────────────────────────────
    public async Task<IActionResult> DiscountReasons()
    {
        ViewData["Title"] = "POS Discount Reasons";
        var reasons = await _db.PosDiscountReasons
            .Include(r => r.Presets.OrderBy(p => p.SortOrder))
            .OrderBy(r => r.SortOrder)
            .ToListAsync();
        return View(reasons);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateReason(string name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var maxSort = await _db.PosDiscountReasons.MaxAsync(r => (int?)r.SortOrder) ?? 0;
            _db.PosDiscountReasons.Add(new PosDiscountReason { Name = name.Trim(), SortOrder = maxSort + 10, IsActive = true });
            await _db.SaveChangesAsync();
        }
        return RedirectToAction(nameof(DiscountReasons));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> EditReason(int id, string name, bool isActive)
    {
        var reason = await _db.PosDiscountReasons.FindAsync(id);
        if (reason != null && !string.IsNullOrWhiteSpace(name))
        {
            reason.Name = name.Trim();
            reason.IsActive = isActive;
            await _db.SaveChangesAsync();
        }
        return RedirectToAction(nameof(DiscountReasons));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteReason(int id)
    {
        var reason = await _db.PosDiscountReasons.FindAsync(id);
        if (reason != null) { _db.PosDiscountReasons.Remove(reason); await _db.SaveChangesAsync(); }
        return RedirectToAction(nameof(DiscountReasons));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreatePreset(int reasonId, string label, string type, decimal value)
    {
        if (!string.IsNullOrWhiteSpace(label) && value > 0)
        {
            var maxSort = await _db.PosDiscountPresets.Where(p => p.ReasonId == reasonId).MaxAsync(p => (int?)p.SortOrder) ?? 0;
            _db.PosDiscountPresets.Add(new PosDiscountPreset
            {
                ReasonId = reasonId, Label = label.Trim(),
                Type = type, Value = value, SortOrder = maxSort + 10
            });
            await _db.SaveChangesAsync();
        }
        return RedirectToAction(nameof(DiscountReasons));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeletePreset(int id)
    {
        var preset = await _db.PosDiscountPresets.FindAsync(id);
        if (preset != null) { _db.PosDiscountPresets.Remove(preset); await _db.SaveChangesAsync(); }
        return RedirectToAction(nameof(DiscountReasons));
    }

    // ── Printable receipt ─────────────────────────────────────────────────────
    public async Task<IActionResult> Receipt(int id)
    {
        var order = await _db.Orders
            .Include(o => o.Items)
            .Include(o => o.PickupStore)
            .FirstOrDefaultAsync(o => o.Id == id && o.Channel == OrderChannel.Pos);
        if (order == null) return NotFound();
        ViewData["Title"] = $"Receipt {order.OrderNumber}";
        return View(order);
    }

    public class SessionRow
    {
        public TillSession Session { get; set; } = null!;
        public int SaleCount { get; set; }
        public decimal Sales { get; set; }
        public decimal ExpectedCash { get; set; }
        public decimal? Variance { get; set; }
    }

    // ── Till sessions oversight (cash-ups / Z-reports) ────────────────────────
    public async Task<IActionResult> Sessions()
    {
        ViewData["Title"] = "Till Sessions";
        var sessions = await _db.TillSessions
            .Include(s => s.Register).ThenInclude(r => r.Store)
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

        var rows = sessions.Select(s =>
        {
            var sa = sales.FirstOrDefault(x => x.Id == s.Id);
            var rf = refunds.FirstOrDefault(x => x.Id == s.Id);
            var expected = s.OpeningFloat + (sa?.Cash ?? 0) - (rf?.CashRef ?? 0);
            return new SessionRow
            {
                Session = s,
                SaleCount = sa?.Count ?? 0,
                Sales = sa?.Total ?? 0,
                ExpectedCash = expected,
                Variance = s.ClosedAt.HasValue ? (s.CountedCash ?? 0) - expected : (decimal?)null
            };
        }).ToList();

        return View(rows);
    }

    // ── POS sales history ─────────────────────────────────────────────────────
    public async Task<IActionResult> Sales()
    {
        ViewData["Title"] = "POS Sales";
        var sales = await _db.Orders
            .Where(o => o.Channel == OrderChannel.Pos)
            .Include(o => o.PickupStore)
            .Include(o => o.Items)
            .OrderByDescending(o => o.CreatedAt)
            .Take(100)
            .ToListAsync();
        return View(sales);
    }
}
