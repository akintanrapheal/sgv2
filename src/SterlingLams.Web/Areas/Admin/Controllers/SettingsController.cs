using Microsoft.AspNetCore.Mvc;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers;

public class SettingsController : AdminBaseController
{
    private readonly ISettingsService _settings;

    public SettingsController(ISettingsService settings) => _settings = settings;

    public async Task<IActionResult> Index(string tab = "General")
    {
        ViewData["Title"] = "Settings";
        ViewData["ActiveTab"] = tab;
        var all = await _settings.GetAllAsync();
        return View(all);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(string group, IFormCollection form)
    {
        var updates = new Dictionary<string, string>();

        // Collect every submitted key that belongs to this group
        foreach (var key in form.Keys.Where(k => k.StartsWith(group.ToLower().Replace(" ", "_") + ".")))
            updates[key] = form[key].ToString();

        // Collect checkbox keys that are MISSING (means unchecked → false)
        var all = await _settings.GetAllAsync();
        foreach (var s in all.Where(s => s.Group == group && s.Type == "boolean"))
        {
            if (!updates.ContainsKey(s.Key))
                updates[s.Key] = "false";
            else
                updates[s.Key] = "true";
        }

        await _settings.SaveManyAsync(updates);
        TempData["Success"] = $"{group} settings saved.";
        return RedirectToAction(nameof(Index), new { tab = group });
    }
}
