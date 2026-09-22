using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Admin.Controllers;

/// <summary>
/// Read-only "Email log" — every email the system attempted to send, so staff can confirm what went
/// out and spot failures. Records the SMTP-level outcome; grantable as its own "EmailLog" section.
/// </summary>
public class EmailLogController : AdminBaseController
{
    protected override string Section => "EmailLog";
    private const int PageSize = 50;

    private readonly ApplicationDbContext _db;
    public EmailLogController(ApplicationDbContext db) => _db = db;

    public async Task<IActionResult> Index(string? status, string? channel, string? q, string? from, string? to, int page = 1)
    {
        ViewData["Title"] = "Email Log";
        if (page < 1) page = 1;

        var query = _db.EmailLogs.AsQueryable();
        if (status == "failed") query = query.Where(e => !e.Sent);
        else if (status == "sent") query = query.Where(e => e.Sent);
        else if (status == "opened") query = query.Where(e => e.OpenedAt != null);
        else if (status == "unopened") query = query.Where(e => e.Sent && e.OpenedAt == null);

        if (!string.IsNullOrWhiteSpace(channel) && new[] { "smtp", "pickup", "skipped" }.Contains(channel))
            query = query.Where(e => e.Channel == channel);

        // Date range (inclusive) on the send time. Parsed as plain dates; CreatedAt is UTC.
        if (DateTime.TryParse(from, out var fromDate))
            query = query.Where(e => e.CreatedAt >= DateTime.SpecifyKind(fromDate.Date, DateTimeKind.Utc));
        if (DateTime.TryParse(to, out var toDate))
            query = query.Where(e => e.CreatedAt < DateTime.SpecifyKind(toDate.Date.AddDays(1), DateTimeKind.Utc));

        if (!string.IsNullOrWhiteSpace(q))
        {
            // ILike, not Contains: Contains compiles to a case-sensitive LIKE on Postgres, so
            // searching "zino@" would miss "Zino@".
            var like = $"%{q.Trim()}%";
            query = query.Where(e => EF.Functions.ILike(e.ToEmail, like) || EF.Functions.ILike(e.Subject, like)
                                  || (e.ToName != null && EF.Functions.ILike(e.ToName, like)));
        }

        var total = await query.CountAsync();
        var items = await query.OrderByDescending(e => e.Id)
            .Skip((page - 1) * PageSize).Take(PageSize).ToListAsync();

        ViewBag.Status = status ?? "";
        ViewBag.Channel = channel ?? "";
        ViewBag.Q = q ?? "";
        ViewBag.From = from ?? "";
        ViewBag.To = to ?? "";
        ViewBag.Page = page;
        ViewBag.TotalPages = (int)System.Math.Ceiling(total / (double)PageSize);
        ViewBag.Total = total;
        ViewBag.SentCount = await _db.EmailLogs.CountAsync(e => e.Sent);
        ViewBag.FailedCount = await _db.EmailLogs.CountAsync(e => !e.Sent);
        ViewBag.OpenedCount = await _db.EmailLogs.CountAsync(e => e.OpenedAt != null);
        return View(items);
    }
}
