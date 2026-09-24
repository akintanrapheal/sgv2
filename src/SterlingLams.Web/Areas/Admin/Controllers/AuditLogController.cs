using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    public class AuditLogController : AdminBaseController
    {
        protected override string Section => "AuditLog";

        private readonly ApplicationDbContext _db;
        private const int PageSize = 50;

        public AuditLogController(ApplicationDbContext db)
        {
            _db = db;
        }

        // NOTE: the action-type filter is named "act", NOT "action" — an "action" parameter binds
        // from the MVC route value {action} ("Index"), which silently filtered out every row.
        public async Task<IActionResult> Index(int page = 1, string act = "", string entity = "",
            string dateFrom = "", string dateTo = "", string q = "")
        {
            ViewData["Title"] = "Audit Log";

            var query = BuildQuery(act, entity, dateFrom, dateTo, q);

            var total = await query.CountAsync();
            var logs = await query
                .OrderByDescending(l => l.CreatedAt)
                .Skip((page - 1) * PageSize)
                .Take(PageSize)
                .Select(l => new AuditLogRow
                {
                    Id = l.Id,
                    Action = l.Action, EntityType = l.EntityType, EntityId = l.EntityId,
                    Description = l.Description, Changes = l.Changes, PerformedBy = l.PerformedBy,
                    IpAddress = l.IpAddress, CreatedAt = l.CreatedAt
                })
                .ToListAsync();

            // Show the staff member's name, not their email. Older / nameless-at-write-time entries
            // stored the email (UserName fallback); resolve those to the current name where we can.
            var emailPerformers = logs.Where(r => r.PerformedBy.Contains('@'))
                .Select(r => r.PerformedBy).Distinct().ToList();
            if (emailPerformers.Count > 0)
            {
                var byEmail = (await _db.Users
                        .Where(u => u.Email != null && emailPerformers.Contains(u.Email))
                        .Select(u => new { u.Email, u.FirstName, u.LastName })
                        .ToListAsync())
                    .ToDictionary(u => u.Email!, u => $"{u.FirstName} {u.LastName}".Trim(), StringComparer.OrdinalIgnoreCase);
                foreach (var r in logs)
                    if (r.PerformedBy.Contains('@') && byEmail.TryGetValue(r.PerformedBy, out var nm) && !string.IsNullOrWhiteSpace(nm))
                        r.PerformedBy = nm;
            }

            // Resolve any user-id GUIDs — the entity id on Account/POS login rows, and any service-
            // supplied performer id (older rows) — to the staff member's name, so the log never shows a
            // raw id. User ids are GUID strings; a GUID that isn't a user simply stays as-is.
            var idSet = logs.Where(r => Guid.TryParse(r.EntityId, out _)).Select(r => r.EntityId)
                .Concat(logs.Where(r => Guid.TryParse(r.PerformedBy, out _)).Select(r => r.PerformedBy))
                .Distinct().ToList();
            if (idSet.Count > 0)
            {
                var byId = (await _db.Users.Where(u => idSet.Contains(u.Id))
                        .Select(u => new { u.Id, u.FirstName, u.LastName, u.UserName }).ToListAsync())
                    .ToDictionary(u => u.Id, u => { var n = $"{u.FirstName} {u.LastName}".Trim(); return string.IsNullOrWhiteSpace(n) ? (u.UserName ?? "") : n; });
                foreach (var r in logs)
                {
                    if (Guid.TryParse(r.EntityId, out _) && byId.TryGetValue(r.EntityId, out var en) && en.Length > 0) r.EntityName = en;
                    if (Guid.TryParse(r.PerformedBy, out _) && byId.TryGetValue(r.PerformedBy, out var pn) && pn.Length > 0) r.PerformedBy = pn;
                }
            }

            var availableActions  = await _db.AuditLogs.Select(l => l.Action).Distinct().OrderBy(a => a).ToListAsync();
            var availableEntities = await _db.AuditLogs.Select(l => l.EntityType).Distinct().OrderBy(e => e).ToListAsync();

            return View(new AdminAuditLogViewModel
            {
                Logs = logs,
                CurrentPage = page,
                TotalPages = (int)Math.Ceiling(total / (double)PageSize),
                ActionFilter = act, EntityFilter = entity, DateFrom = dateFrom, DateTo = dateTo,
                SearchQuery = q,
                AvailableActions = availableActions, AvailableEntities = availableEntities
            });
        }

        // Full detail for one audit entry (row click) — everything in one place, with the long
        // description / changes shown in full and any user id resolved to the staff member's name.
        public async Task<IActionResult> Detail(int id)
        {
            var log = await _db.AuditLogs.FirstOrDefaultAsync(l => l.Id == id);
            if (log == null) return NotFound();
            ViewData["Title"] = $"Audit — {log.Action}";

            async Task<string?> NameAsync(string? v)
            {
                if (string.IsNullOrWhiteSpace(v)) return null;
                var u = await _db.Users.Where(x => x.Id == v || x.Email == v || x.UserName == v)
                    .Select(x => new { x.FirstName, x.LastName, x.UserName }).FirstOrDefaultAsync();
                if (u == null) return null;
                var n = $"{u.FirstName} {u.LastName}".Trim();
                return string.IsNullOrWhiteSpace(n) ? u.UserName : n;
            }
            ViewBag.EntityName = Guid.TryParse(log.EntityId, out _) ? await NameAsync(log.EntityId) : null;
            ViewBag.PerformerName = await NameAsync(log.PerformedBy);   // null when already a name / label
            return View(log);
        }

        public async Task<IActionResult> ExportCsv(string act = "", string entity = "",
            string dateFrom = "", string dateTo = "", string q = "")
        {
            var logs = await BuildQuery(act, entity, dateFrom, dateTo, q)
                .OrderByDescending(l => l.CreatedAt)
                .ToListAsync();

            // Csv handles the escaping (only two columns were escaped here) and neutralises values a
            // spreadsheet would execute — audit descriptions contain customer- and staff-typed text.
            var sb = new StringBuilder();
            SterlingLams.Web.Services.Csv.AppendRow(sb, "Timestamp (WAT)", "Action", "Entity Type",
                "Entity ID", "Description", "Changes", "Performed By", "IP Address");
            foreach (var l in logs)
            {
                SterlingLams.Web.Services.Csv.AppendRow(sb,
                    SterlingLams.Web.Services.ReportCalendar.ToLocal(l.CreatedAt).ToString("yyyy-MM-dd HH:mm:ss"),
                    l.Action, l.EntityType, l.EntityId, l.Description,
                    (l.Changes ?? "").Replace("\n", "; "),
                    l.PerformedBy, l.IpAddress);
            }

            await LogAsync("Export", "AuditLog", null, $"Exported {logs.Count} audit log entries to CSV");

            return File(SterlingLams.Web.Services.Csv.ToBytes(sb), "text/csv",
                $"audit_log_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv");
        }

        // Deleting audit history is restricted to the OWNER account (AdminSections.IsOwner) — not
        // merely any full administrator. The audit log is the accountability record, so nobody who
        // could be added as an admin later can quietly erase it.
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteSelected(int[] ids, string act = "", string entity = "",
            string dateFrom = "", string dateTo = "", string q = "", int page = 1)
        {
            if (!AdminSections.IsOwner(User)) return Forbid();
            var back = new { act, entity, dateFrom, dateTo, q, page };
            if (ids == null || ids.Length == 0)
            {
                TempData["Error"] = "No log entries were selected.";
                return RedirectToAction(nameof(Index), back);
            }
            var deleted = await _db.AuditLogs.Where(l => ids.Contains(l.Id)).ExecuteDeleteAsync();
            await LogAsync("Delete", "AuditLog", null, $"Deleted {deleted} audit log entr{(deleted == 1 ? "y" : "ies")}");
            TempData["Success"] = $"{deleted} log entr{(deleted == 1 ? "y" : "ies")} deleted.";
            return RedirectToAction(nameof(Index), back);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ClearAll()
        {
            if (!AdminSections.IsOwner(User)) return Forbid();
            var deleted = await _db.AuditLogs.ExecuteDeleteAsync();
            // Leave a single record that a clear happened (accountability).
            await LogAsync("Delete", "AuditLog", null, $"Cleared all audit logs ({deleted} entries)");
            TempData["Success"] = $"Cleared all {deleted} audit log entries.";
            return RedirectToAction(nameof(Index));
        }

        private IQueryable<AuditLog> BuildQuery(string action, string entity, string dateFrom, string dateTo, string q)
        {
            var query = _db.AuditLogs.AsQueryable();

            if (!string.IsNullOrWhiteSpace(action))
                query = query.Where(l => l.Action == action);

            if (!string.IsNullOrWhiteSpace(entity))
                query = query.Where(l => l.EntityType == entity);

            // Lagos dates → UTC instants, the same calendar the money reports use. This used to lean on
            // ToUniversalTime() (i.e. the server's local zone), which is only right by accident.
            if (DateTime.TryParse(dateFrom, out var from))
                query = query.Where(l => l.CreatedAt >= SterlingLams.Web.Services.ReportCalendar.StartOfDayUtc(from));

            if (DateTime.TryParse(dateTo, out var to))
                query = query.Where(l => l.CreatedAt < SterlingLams.Web.Services.ReportCalendar.StartOfDayUtc(to.AddDays(1)));

            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(l =>
                    EF.Functions.ILike(l.Description, $"%{q}%") ||
                    EF.Functions.ILike(l.PerformedBy, $"%{q}%"));

            return query;
        }
    }
}
