using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Admin.Controllers;

/// <summary>Admin → Customer Issues: a business-facing monitor of problems customers hit while trying to
/// buy — failed add-to-cart, payment failures, checkout blocks and unexpected errors — so the owner can
/// see and fix what's costing sales. Records are written best-effort across the storefront/checkout; this
/// is the read + triage surface.</summary>
public class CustomerIssuesController : AdminBaseController
{
    protected override string Section => "CustomerIssues";

    private readonly ApplicationDbContext _db;
    private const int PageSize = 50;

    public CustomerIssuesController(ApplicationDbContext db) => _db = db;

    public async Task<IActionResult> Index(string type = "", string dateFrom = "", string dateTo = "",
        string resolved = "", string q = "", int page = 1)
    {
        ViewData["Title"] = "Customer Issues";

        var now = DateTime.UtcNow;
        var todayStart = Services.ReportCalendar.StartOfDayUtc(Services.ReportCalendar.Today);
        var weekStart = todayStart.AddDays(-6);

        // ── Summary cards ───────────────────────────────────────────────────
        var weekRows = await _db.StorefrontIssues
            .Where(i => i.CreatedAt >= weekStart)
            .Select(i => new { i.Type, i.CreatedAt })
            .ToListAsync();
        Dictionary<StorefrontIssueType, int> ByType(IEnumerable<StorefrontIssueType> src) =>
            Enum.GetValues<StorefrontIssueType>().ToDictionary(t => t, t => src.Count(x => x == t));
        var todayByType = ByType(weekRows.Where(r => r.CreatedAt >= todayStart).Select(r => r.Type));
        var weekByType = ByType(weekRows.Select(r => r.Type));
        var unresolved = await _db.StorefrontIssues.CountAsync(i => !i.Resolved);

        // 14-day trend (one point per Lagos day).
        var trendStart = todayStart.AddDays(-13);
        var trendRaw = await _db.StorefrontIssues.Where(i => i.CreatedAt >= trendStart)
            .Select(i => i.CreatedAt).ToListAsync();
        var trend = Enumerable.Range(0, 14)
            .Select(d => todayStart.AddDays(-13 + d))
            .Select(day => new CustomerIssueTrendPoint(
                Services.ReportCalendar.ToLocal(day).ToString("MMM d"),
                trendRaw.Count(c => c >= day && c < day.AddDays(1))))
            .ToList();

        // Abandoned payments (unpaid → auto-cancelled online orders) over the last 7 days — the ₦ that
        // nearly came through. Derived from orders, not the issue log.
        var abandonedQ = _db.Orders.Where(o => o.Channel == OrderChannel.Online && !o.IsPaid
            && o.Status == OrderStatus.Cancelled && o.CreatedAt >= weekStart);
        var abandonedCount = await abandonedQ.CountAsync();
        var abandonedValue = abandonedCount == 0 ? 0m : await abandonedQ.SumAsync(o => o.Total);

        // ── Feed (filtered + paged) ─────────────────────────────────────────
        var query = _db.StorefrontIssues.AsQueryable();
        if (Enum.TryParse<StorefrontIssueType>(type, true, out var t))
            query = query.Where(i => i.Type == t);
        if (DateTime.TryParse(dateFrom, out var from))
            query = query.Where(i => i.CreatedAt >= Services.ReportCalendar.StartOfDayUtc(from));
        if (DateTime.TryParse(dateTo, out var to))
            query = query.Where(i => i.CreatedAt < Services.ReportCalendar.StartOfDayUtc(to.AddDays(1)));
        if (resolved == "open") query = query.Where(i => !i.Resolved);
        else if (resolved == "done") query = query.Where(i => i.Resolved);
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(i =>
                EF.Functions.ILike(i.Reason, $"%{q}%") ||
                (i.Message != null && EF.Functions.ILike(i.Message, $"%{q}%")) ||
                (i.ProductName != null && EF.Functions.ILike(i.ProductName, $"%{q}%")) ||
                (i.OrderNumber != null && EF.Functions.ILike(i.OrderNumber, $"%{q}%")) ||
                (i.ContactEmail != null && EF.Functions.ILike(i.ContactEmail, $"%{q}%")));

        var total = await query.CountAsync();
        var rows = await query
            .OrderBy(i => i.Resolved)                 // unresolved first
            .ThenByDescending(i => i.CreatedAt)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .ToListAsync();

        return View(new CustomerIssuesVm
        {
            Type = type, DateFrom = dateFrom, DateTo = dateTo, Resolved = resolved, Q = q,
            Page = page, TotalPages = (int)Math.Ceiling(total / (double)PageSize), TotalRows = total,
            TodayByType = todayByType, WeekByType = weekByType,
            TodayTotal = todayByType.Values.Sum(), WeekTotal = weekByType.Values.Sum(),
            Unresolved = unresolved, Trend = trend,
            AbandonedCount = abandonedCount, AbandonedValue = abandonedValue,
            Rows = rows
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Resolve(int id, string returnUrl = "")
    {
        var issue = await _db.StorefrontIssues.FirstOrDefaultAsync(i => i.Id == id);
        if (issue != null && !issue.Resolved)
        {
            issue.Resolved = true;
            issue.ResolvedAt = DateTime.UtcNow;
            issue.ResolvedByName = User.Identity?.Name;
            await _db.SaveChangesAsync();
        }
        return LocalRedirect(string.IsNullOrEmpty(returnUrl) ? Url.Action(nameof(Index))! : returnUrl);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResolveAll(string type = "")
    {
        var q = _db.StorefrontIssues.Where(i => !i.Resolved);
        if (Enum.TryParse<StorefrontIssueType>(type, true, out var t)) q = q.Where(i => i.Type == t);
        var who = User.Identity?.Name;
        var n = await q.ExecuteUpdateAsync(s => s
            .SetProperty(i => i.Resolved, true)
            .SetProperty(i => i.ResolvedAt, DateTime.UtcNow)
            .SetProperty(i => i.ResolvedByName, who));
        TempData["Success"] = $"Marked {n} issue(s) resolved.";
        return RedirectToAction(nameof(Index));
    }

    public record CustomerIssueTrendPoint(string Label, int Count);

    public class CustomerIssuesVm
    {
        public string Type = "", DateFrom = "", DateTo = "", Resolved = "", Q = "";
        public int Page, TotalPages, TotalRows;
        public Dictionary<StorefrontIssueType, int> TodayByType = new();
        public Dictionary<StorefrontIssueType, int> WeekByType = new();
        public int TodayTotal, WeekTotal, Unresolved;
        public List<CustomerIssueTrendPoint> Trend = new();
        public int AbandonedCount;
        public decimal AbandonedValue;
        public List<StorefrontIssue> Rows = new();
    }
}
