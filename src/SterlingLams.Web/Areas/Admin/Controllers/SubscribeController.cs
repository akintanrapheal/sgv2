using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers;

/// <summary>
/// Mock "API connector" subscription / billing page. Presents each store with a (deterministic,
/// fake) API key and a per-store fee, and lets the owner "subscribe" monthly or yearly. No real
/// payment is taken and no schema is touched — state lives entirely in site settings, so it can
/// never break anything. Full administrators only (Section == null).
/// </summary>
public class SubscribeController : AdminBaseController
{
    private readonly ApplicationDbContext _db;
    private readonly ISettingsService _settings;

    public SubscribeController(ApplicationDbContext db, ISettingsService settings)
    {
        _db = db;
        _settings = settings;
    }

    /// <summary>Per-store price (USD). Yearly gives two months free.</summary>
    public const decimal PerStoreMonthly = 50m;
    public const decimal PerStoreYearly = 500m;

    /// <summary>Deterministic, stable-looking fake API key for a store.</summary>
    public static string FakeKey(int storeId, string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"sglams-connector:{storeId}:{name}"));
        return "sk_live_sg_" + Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Subscribe";
        var stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();

        ViewBag.Stores = stores;
        ViewBag.PerStoreMonthly = PerStoreMonthly;
        ViewBag.PerStoreYearly = PerStoreYearly;
        ViewBag.MonthlyTotal = stores.Count * PerStoreMonthly;
        ViewBag.YearlyTotal = stores.Count * PerStoreYearly;
        ViewBag.Keys = stores.ToDictionary(s => s.Id, s => FakeKey(s.Id, s.Name));

        ViewBag.Subscribed = await _settings.GetBoolAsync("subscription.active", false);
        ViewBag.Plan = await _settings.GetAsync("subscription.plan", "monthly");
        ViewBag.RenewsOn = await _settings.GetAsync("subscription.renews_on", "");
        ViewBag.TrialEnds = await _settings.GetAsync("general.trial_notice_date", "2026-07-30");
        ViewBag.NoticeEnabled = await _settings.GetBoolAsync("general.trial_notice_enabled", true);
        ViewBag.NoticeMessage = await _settings.GetAsync("general.trial_notice_message", "");
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Pay(string plan)
    {
        plan = plan == "yearly" ? "yearly" : "monthly";
        var renews = (plan == "yearly" ? DateTime.UtcNow.AddYears(1) : DateTime.UtcNow.AddMonths(1)).ToString("yyyy-MM-dd");

        await _settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["subscription.active"] = "true",
            ["subscription.plan"] = plan,
            ["subscription.renews_on"] = renews,
            ["general.trial_notice_enabled"] = "false", // stop the amber trial warning
        });
        await LogAsync("Update", "Subscription", null, $"Activated API connector subscription ({plan}); renews {renews}");
        TempData["Success"] = "Payment successful — your API connector is active and all stores are synchronising.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Owner-only management of the staff-wide trial banner (shown to everyone; edited only here).</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveNotice(bool enabled, string? date, string? message)
    {
        var d = DateTime.TryParse(date, out var dt) ? dt.ToString("yyyy-MM-dd")
                                                    : (string.IsNullOrWhiteSpace(date) ? "2026-07-30" : date.Trim());
        await _settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["general.trial_notice_enabled"] = enabled ? "true" : "false",
            ["general.trial_notice_date"] = d,
            ["general.trial_notice_message"] = (message ?? "").Trim(),
        });
        await LogAsync("Update", "Subscription", null, $"Updated trial connector notice (enabled={enabled}, expires {d})");
        TempData["Success"] = "Notice updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel()
    {
        await _settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["subscription.active"] = "false",
            ["general.trial_notice_enabled"] = "true", // bring the trial warning back
        });
        await LogAsync("Update", "Subscription", null, "Cancelled API connector subscription");
        TempData["Success"] = "Subscription cancelled — your stores are back on the trial connector.";
        return RedirectToAction(nameof(Index));
    }
}
