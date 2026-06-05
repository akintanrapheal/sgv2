using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    public class AuditLogController : AdminBaseController
    {
        private readonly ApplicationDbContext _db;
        private const int PageSize = 50;

        public AuditLogController(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task<IActionResult> Index(int page = 1)
        {
            ViewData["Title"] = "Audit Log";

            var total = await _db.AuditLogs.CountAsync();
            var logs = await _db.AuditLogs
                .OrderByDescending(l => l.CreatedAt)
                .Skip((page - 1) * PageSize)
                .Take(PageSize)
                .Select(l => new AuditLogRow
                {
                    Action = l.Action,
                    EntityType = l.EntityType,
                    EntityId = l.EntityId,
                    Description = l.Description,
                    PerformedBy = l.PerformedBy,
                    CreatedAt = l.CreatedAt
                })
                .ToListAsync();

            return View(new AdminAuditLogViewModel
            {
                Logs = logs,
                CurrentPage = page,
                TotalPages = (int)Math.Ceiling(total / (double)PageSize)
            });
        }
    }
}
