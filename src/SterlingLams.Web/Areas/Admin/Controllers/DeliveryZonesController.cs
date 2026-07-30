using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers;

/// <summary>
/// Distance-based delivery zones for Lagos &amp; Abuja. Each zone carries its own Standard + Express
/// fees/timeframes and the list of areas it covers; the whole set is persisted as the
/// <c>shipping.delivery_zones</c> JSON setting (no schema/migration needed).
/// </summary>
public class DeliveryZonesController : AdminBaseController
{
    protected override string Section => "Settings";
    protected override bool EnforceManageOnWrite => false; // parity with SettingsController

    // The zones live in the "Shipping" settings group.
    private const string Group = "Shipping";

    private readonly DeliveryZoneService _zones;
    private readonly ISettingsService _settings;
    private readonly IPermissionService _perms;

    public DeliveryZonesController(DeliveryZoneService zones, ISettingsService settings,
        IPermissionService perms)
    {
        _zones = zones;
        _settings = settings;
        _perms = perms;
    }

    /// <summary>
    /// Write access mirrors SettingsController: because <see cref="EnforceManageOnWrite"/> is off, the
    /// base class only checks Settings *view* on a POST, so the group grant must be checked here or a
    /// view-only role could rewrite every delivery fee.
    /// </summary>
    private async Task<bool> CanEditAsync()
    {
        var allowed = await _perms.GetAllowedSettingsGroupsAsync(User);
        return allowed == null || allowed.Contains(Group);
    }

    public async Task<IActionResult> Index()
    {
        // Don't show an editor the user can't save — Settings hides groups the same way.
        if (!await CanEditAsync())
            return RedirectToAction("AccessDenied", "Account", new { area = "" });

        var zones = await _zones.GetZonesAsync();
        return View(zones);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save([FromBody] List<DeliveryZoneDef>? zones)
    {
        if (!await CanEditAsync())
            return StatusCode(403, new { ok = false, error = "You don't have access to shipping settings." });

        var clean = (zones ?? new())
            .Where(z => !string.IsNullOrWhiteSpace(z.Name))
            .Select(z => new DeliveryZoneDef
            {
                State        = string.Equals(z.State, "Abuja", StringComparison.OrdinalIgnoreCase) ? "Abuja" : "Lagos",
                Name         = z.Name.Trim(),
                StandardFee  = Math.Max(0, z.StandardFee),
                ExpressFee   = Math.Max(0, z.ExpressFee),
                StandardDays = string.IsNullOrWhiteSpace(z.StandardDays) ? "2 - 4 working days" : z.StandardDays.Trim(),
                ExpressDays  = string.IsNullOrWhiteSpace(z.ExpressDays)  ? "24 - 48 hours"     : z.ExpressDays.Trim(),
                Areas        = (z.Areas ?? new())
                    .Select(a => (a ?? "").Trim())
                    .Where(a => a.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            })
            .ToList();

        var json = JsonSerializer.Serialize(clean);
        await _settings.SaveManyAsync(new Dictionary<string, string> { ["shipping.delivery_zones"] = json });

        await LogAsync("Update", "Setting", "shipping.delivery_zones",
            $"Updated delivery zones ({clean.Count} zone(s): "
            + $"{clean.Count(z => z.State == "Lagos")} Lagos, {clean.Count(z => z.State == "Abuja")} Abuja)");

        return Json(new { ok = true, count = clean.Count });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reset()
    {
        if (!await CanEditAsync())
            return StatusCode(403, new { ok = false, error = "You don't have access to shipping settings." });

        // Return the built-in defaults to the editor (does not persist until Save).
        return Json(new { ok = true, zones = DeliveryZoneService.DefaultZones() });
    }
}
