using Microsoft.AspNetCore.Mvc;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers;

public class SettingsController : AdminBaseController
{
    protected override string Section => "Settings";

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
        var all = await _settings.GetAllAsync();
        var groupSettings = all.Where(s => s.Group == group).ToList();

        // Collect every setting key that belongs to this group
        foreach (var s in groupSettings)
        {
            if (s.Type == "boolean")
                updates[s.Key] = form.ContainsKey(s.Key) ? "true" : "false";
            else if (form.ContainsKey(s.Key))
                updates[s.Key] = form[s.Key].ToString();
        }

        await _settings.SaveManyAsync(updates);
        await LogAsync("Update", "Setting", null,
            $"Updated {group} settings ({updates.Count} field(s))");
        TempData["Success"] = $"{group} settings saved.";
        return RedirectToAction(nameof(Index), new { tab = group });
    }

    // Persist a single setting immediately (used by the image uploader so the
    // value is saved without waiting for the user to click "Save").
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveOne(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
            return BadRequest(new { error = "Missing setting key." });

        await _settings.SaveManyAsync(new Dictionary<string, string> { [key] = value ?? string.Empty });
        await LogAsync("Update", "Setting", key, $"Updated setting '{key}'");
        return Ok(new { success = true });
    }
}
