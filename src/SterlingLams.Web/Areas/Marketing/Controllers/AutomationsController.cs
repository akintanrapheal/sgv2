using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Marketing.Controllers;

public class AutomationsController : MarketingAreaController
{
    private readonly ApplicationDbContext _db;
    public AutomationsController(ApplicationDbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Automations";
        var rows = await _db.Automations.AsNoTracking().OrderByDescending(a => a.CreatedAt).ToListAsync();
        return View(rows);
    }

    public IActionResult Create()
    {
        ViewData["Title"] = "New Automation";
        return View("Edit", new Automation());
    }

    public async Task<IActionResult> Edit(int id)
    {
        var a = await _db.Automations.FindAsync(id);
        if (a == null) return NotFound();
        ViewData["Title"] = "Edit Automation";
        return View(a);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Automation vm)
    {
        if (string.IsNullOrWhiteSpace(vm.Name) || string.IsNullOrWhiteSpace(vm.Subject))
        {
            TempData["Error"] = "Name and subject are required.";
            return View("Edit", vm);
        }

        var isNew = vm.Id == 0;
        var a = isNew ? new Automation() : await _db.Automations.FindAsync(vm.Id);
        if (a == null) return NotFound();

        a.Name = vm.Name.Trim();
        a.Trigger = vm.Trigger;
        a.WinBackDays = vm.WinBackDays < 1 ? 90 : vm.WinBackDays;
        a.DelayHours = vm.DelayHours < 0 ? 0 : vm.DelayHours;
        a.Subject = vm.Subject.Trim();
        a.BodyHtml = ProductHtml.Sanitize(vm.BodyHtml ?? "");
        a.UpdatedAt = DateTime.UtcNow;
        if (isNew)
        {
            a.CreatedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            _db.Automations.Add(a);
        }
        await _db.SaveChangesAsync();
        await LogAsync(isNew ? "Create" : "Update", "Automation", a.Id.ToString(), $"{(isNew ? "Created" : "Updated")} automation '{a.Name}'");
        TempData["Success"] = "Automation saved.";
        return RedirectToAction(nameof(Detail), new { id = a.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetActive(int id, bool active)
    {
        var a = await _db.Automations.FindAsync(id);
        if (a != null)
        {
            if (active && string.IsNullOrWhiteSpace(a.BodyHtml))
            {
                TempData["Error"] = "Add an email body before activating.";
                return RedirectToAction(nameof(Detail), new { id });
            }
            a.IsActive = active;
            // Stamp the activation cutoff the first time it's switched on so it never back-emails
            // the whole customer history.
            if (active && a.ActivatedAt == null) a.ActivatedAt = DateTime.UtcNow;
            a.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await LogAsync("Update", "Automation", id.ToString(), $"{(active ? "Activated" : "Paused")} automation '{a.Name}'");
            TempData["Success"] = active ? "Automation activated." : "Automation paused.";
        }
        return RedirectToAction(nameof(Detail), new { id });
    }

    public async Task<IActionResult> Detail(int id)
    {
        var a = await _db.Automations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (a == null) return NotFound();
        ViewData["Title"] = a.Name;
        ViewBag.Enrolled = await _db.AutomationRuns.CountAsync(r => r.AutomationId == id);
        ViewBag.Sent = await _db.AutomationRuns.CountAsync(r => r.AutomationId == id && r.Status == AutomationRunStatus.Sent);
        ViewBag.Pending = await _db.AutomationRuns.CountAsync(r => r.AutomationId == id && r.Status == AutomationRunStatus.Pending);
        return View(a);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var a = await _db.Automations.FindAsync(id);
        if (a != null)
        {
            _db.Automations.Remove(a); // runs cascade
            await _db.SaveChangesAsync();
            await LogAsync("Delete", "Automation", id.ToString(), $"Deleted automation '{a.Name}'");
            TempData["Success"] = "Automation deleted.";
        }
        return RedirectToAction(nameof(Index));
    }
}
