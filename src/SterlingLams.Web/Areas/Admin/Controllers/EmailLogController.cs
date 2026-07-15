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

    public async Task<IActionResult> Index(string? status, string? q, int page = 1)
    {
        ViewData["Title"] = "Email Log";
        if (page < 1) page = 1;

        var query = _db.EmailLogs.AsQueryable();
        if (status == "failed") query = query.Where(e => !e.Sent);
        else if (status == "sent") query = query.Where(e => e.Sent);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(e => e.ToEmail.Contains(term) || e.Subject.Contains(term));
        }

        var total = await query.CountAsync();
        var items = await query.OrderByDescending(e => e.Id)
            .Skip((page - 1) * PageSize).Take(PageSize).ToListAsync();

        ViewBag.Status = status ?? "";
        ViewBag.Q = q ?? "";
        ViewBag.Page = page;
        ViewBag.TotalPages = (int)System.Math.Ceiling(total / (double)PageSize);
        ViewBag.Total = total;
        ViewBag.SentCount = await _db.EmailLogs.CountAsync(e => e.Sent);
        ViewBag.FailedCount = await _db.EmailLogs.CountAsync(e => !e.Sent);
        return View(items);
    }
}
