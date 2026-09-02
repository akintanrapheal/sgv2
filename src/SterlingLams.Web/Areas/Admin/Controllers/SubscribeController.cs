using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using SterlingLams.Web.Services.Payment;

namespace SterlingLams.Web.Areas.Admin.Controllers;

/// <summary>
/// "API connector" subscription / billing page. Each active store carries a per-store fee; the admin
/// pays via Paystack (credited to the DEVELOPER's account, separate from the storefront checkout).
/// On a verified payment the subscription is activated until the next billing date and the staff-wide
/// trial banner is switched off. Restricted to Admin and Developer only (Owner is excluded).
/// </summary>
public class SubscribeController : AdminBaseController
{
    private readonly ApplicationDbContext _db;
    private readonly ISettingsService _settings;
    private readonly ISettingsSecretProtector _secrets;
    private readonly ISubscriptionPaymentService _pay;
    private readonly IZephielClient _zephiel;

    public SubscribeController(ApplicationDbContext db, ISettingsService settings,
        ISettingsSecretProtector secrets, ISubscriptionPaymentService pay, IZephielClient zephiel)
    {
        _db = db;
        _settings = settings;
        _secrets = secrets;
        _pay = pay;
        _zephiel = zephiel;
    }

    /// <summary>Parses the zephiel.store_keys JSON blob (store id → per-store Zephiel key).</summary>
    private static Dictionary<string, string> ParseStoreKeys(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new(); }
        catch { return new(); }
    }

    // Billing/subscription is restricted to the configured OWNER ACCOUNT ONLY (Admin:OwnerEmails —
    // default rapheal@sterlinglamslogistics.com), not merely a role. So no other account — even a
    // full-access Admin/Owner/Developer created for staff — can view or change billing. Gates EVERY
    // action here (Index included); matches the Subscribe nav visibility in _AdminLayout.
    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!AdminSections.IsOwner(User))
        {
            context.Result = RedirectToAction("AccessDenied", "Account", new { area = "" });
            return;
        }
        await base.OnActionExecutionAsync(context, next);
    }

    /// <summary>Belt-and-suspenders per-action guard (the controller is already owner-only above).</summary>
    private IActionResult? RequireManager() =>
        AdminSections.IsOwner(User) ? null : RedirectToAction("AccessDenied", "Account", new { area = "" });

    /// <summary>Per-store prices in USD (shown to the admin; charged in NGN at the current rate).</summary>
    private async Task<(decimal monthly, decimal yearly)> PricingAsync()
    {
        var m = decimal.TryParse(await _settings.GetAsync("subscription.price_monthly", "50"), out var mm) ? mm : 50m;
        var y = decimal.TryParse(await _settings.GetAsync("subscription.price_yearly", "550"), out var yy) ? yy : 550m;
        return (m, y);
    }

    // Owner may view the page and pay (Index/Pay/PaymentCallback); only Admin + Developer may change the
    // billing config or the notice (guarded per-action below). The base controller already limits the
    // whole controller to full-access roles.

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

        var (perMonthly, perYearly) = await PricingAsync();
        var rate = await _pay.GetUsdToNgnAsync();
        ViewBag.Stores = stores;
        ViewBag.PerStoreMonthly = perMonthly; // USD
        ViewBag.PerStoreYearly = perYearly;   // USD
        ViewBag.MonthlyTotal = stores.Count * perMonthly;
        ViewBag.YearlyTotal = stores.Count * perYearly;
        ViewBag.Rate = rate;                                            // USD → NGN
        ViewBag.MonthlyTotalNgn = Math.Round(stores.Count * perMonthly * rate);
        ViewBag.YearlyTotalNgn = Math.Round(stores.Count * perYearly * rate);
        ViewBag.PayConfigured = await _pay.IsConfiguredAsync();
        ViewBag.CanManage = AdminSections.IsOwner(User); // only the owner account reaches this page, and may configure
        ViewBag.PaystackSecretSet = !string.IsNullOrWhiteSpace(await _settings.GetAsync("subscription.paystack_secret"));
        ViewBag.RateOverride = await _settings.GetAsync("subscription.usd_to_ngn", "");
        // Show the REAL Zephiel per-store keys so they match Zephiel exactly; fall back to a stable
        // placeholder for any store not provisioned on Zephiel yet.
        var zephielKeys = ParseStoreKeys(await _settings.GetAsync("zephiel.store_keys", ""));
        ViewBag.Keys = stores.ToDictionary(s => s.Id,
            s => zephielKeys.TryGetValue(s.Id.ToString(), out var zk) && !string.IsNullOrWhiteSpace(zk)
                ? zk : FakeKey(s.Id, s.Name));

        // Zephiel connector configuration, surfaced on this page.
        ViewBag.ZephEnabled = await _settings.GetBoolAsync("zephiel.enabled", false);
        ViewBag.ZephBaseUrl = await _settings.GetAsync("zephiel.base_url", "https://www.zephiel.com");
        ViewBag.ZephApiSlug = await _settings.GetAsync("zephiel.api_slug", "multistore");
        ViewBag.ZephAccountKeySet = !string.IsNullOrWhiteSpace(await _settings.GetAsync("zephiel.account_key", ""));

        ViewBag.Subscribed = await _settings.GetBoolAsync("subscription.active", false);
        ViewBag.Plan = await _settings.GetAsync("subscription.plan", "monthly");
        ViewBag.RenewsOn = await _settings.GetAsync("subscription.renews_on", "");
        ViewBag.TrialEnds = await _settings.GetAsync("general.trial_notice_date", "2026-07-30");
        ViewBag.NoticeEnabled = await _settings.GetBoolAsync("general.trial_notice_enabled", true);
        ViewBag.NoticeMessage = await _settings.GetAsync("general.trial_notice_message", "");
        return View();
    }

    /// <summary>Live usage feed for the Subscribe-page chart (polled ~30s by the browser). Proxies
    /// Zephiel's usage summary (30-day series, per-store, quota); returns { ok:false } when the connector
    /// is off/unconfigured or Zephiel is unavailable, so the chart degrades quietly. Owner-only via the
    /// controller filter above.</summary>
    [HttpGet]
    public async Task<IActionResult> ZephielUsage()
    {
        var json = await _zephiel.GetUsageAsync();
        if (string.IsNullOrWhiteSpace(json)) return Json(new { ok = false });
        return Content(json, "application/json");
    }

    // Start a real Paystack payment for the subscription, then redirect the admin to Paystack.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Pay(string plan)
    {
        plan = plan == "yearly" ? "yearly" : "monthly";
        var storeCount = await _db.Stores.CountAsync(s => s.IsActive);
        var (perMonthly, perYearly) = await PricingAsync();
        var usdTotal = storeCount * (plan == "yearly" ? perYearly : perMonthly);
        if (usdTotal <= 0)
        {
            TempData["Error"] = "Nothing to charge — add an active store and set a per-store price first.";
            return RedirectToAction(nameof(Index));
        }

        // Prices are shown in USD but the merchant's Paystack only supports NGN — convert at the
        // current rate and charge in Naira.
        var rate = await _pay.GetUsdToNgnAsync();
        var nairaAmount = Math.Round(usdTotal * rate, MidpointRounding.AwayFromZero);

        var email = User.Identity?.Name ?? await _settings.GetAsync("general.contact_email", "");
        var reference = "SGSUB-" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
        var callback = Url.Action(nameof(PaymentCallback), "Subscribe",
            new { area = "Admin", reference, plan }, Request.Scheme)!;

        var (ok, url, _, error) = await _pay.InitializeAsync(email, nairaAmount, "NGN", callback, reference,
            $"API connector subscription — {plan}, {storeCount} store(s), ${usdTotal:0.##} @ ₦{rate:0.##}/$", plan);
        if (!ok || string.IsNullOrEmpty(url))
        {
            TempData["Error"] = error ?? "Could not start the payment.";
            return RedirectToAction(nameof(Index));
        }
        return Redirect(url); // off to Paystack's hosted checkout
    }

    // Paystack redirects the admin back here after payment — verify, then activate on success.
    [HttpGet]
    public async Task<IActionResult> PaymentCallback(string? reference, string? plan)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            TempData["Error"] = "Payment reference was missing — nothing was charged.";
            return RedirectToAction(nameof(Index));
        }

        // Only references this page generated (see Pay) — not any arbitrary paid reference.
        if (!reference.StartsWith("SGSUB-", StringComparison.Ordinal))
        {
            TempData["Error"] = "That payment reference wasn't issued by this page.";
            return RedirectToAction(nameof(Index));
        }

        // Idempotent: reloading the callback URL must not re-activate and push the renewal date out
        // again. The reference of the payment that activated the current subscription is recorded.
        if (string.Equals(await _settings.GetAsync("subscription.last_reference", ""), reference, StringComparison.Ordinal))
        {
            TempData["Success"] = $"That payment is already applied — active until {await _settings.GetAsync("subscription.renews_on", "")}.";
            return RedirectToAction(nameof(Index));
        }

        var (ok, paid, error) = await _pay.VerifyAsync(reference);
        if (!ok)
        {
            TempData["Error"] = "Could not verify the payment: " + (error ?? "unknown error") + ". If you were charged, contact support.";
            return RedirectToAction(nameof(Index));
        }
        if (!paid)
        {
            TempData["Error"] = "Payment was not completed. You can try again.";
            return RedirectToAction(nameof(Index));
        }

        var renews = await _pay.ActivateAsync(plan ?? "monthly");
        await _settings.SaveManyAsync(new Dictionary<string, string> { ["subscription.last_reference"] = reference });
        await LogAsync("Update", "Subscription", null, $"Paystack payment {reference} verified — active, renews {renews}", performedBy: "API System");
        TempData["Success"] = $"Payment received — your API connector is active until {renews}.";
        return RedirectToAction(nameof(Index));
    }

    // Save the developer Paystack key (encrypted), currency and per-store prices.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveBilling(string? paystackSecret, decimal priceMonthly, decimal priceYearly, decimal? usdToNgn)
    {
        if (RequireManager() is IActionResult deny) return deny;
        var updates = new Dictionary<string, string>
        {
            ["subscription.price_monthly"] = (priceMonthly < 0 ? 0 : priceMonthly).ToString("0.##"),
            ["subscription.price_yearly"] = (priceYearly < 0 ? 0 : priceYearly).ToString("0.##"),
            ["subscription.usd_to_ngn"] = usdToNgn is > 0 ? usdToNgn.Value.ToString("0.##") : "",
        };
        // Only replace the key when a new one is entered (blank = keep the existing encrypted value).
        if (!string.IsNullOrWhiteSpace(paystackSecret))
            updates["subscription.paystack_secret"] = _secrets.Protect(paystackSecret.Trim());

        await _settings.SaveManyAsync(updates);
        await LogAsync("Update", "Subscription", null, "Updated subscription billing settings", performedBy: "API System");
        TempData["Success"] = "Billing settings saved.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Owner-only management of the staff-wide trial banner (shown to everyone; edited only here).</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveNotice(bool enabled, string? date, string? message)
    {
        if (RequireManager() is IActionResult deny) return deny;
        var d = DateTime.TryParse(date, out var dt) ? dt.ToString("yyyy-MM-dd")
                                                    : (string.IsNullOrWhiteSpace(date) ? "2026-07-30" : date.Trim());
        await _settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["general.trial_notice_enabled"] = enabled ? "true" : "false",
            ["general.trial_notice_date"] = d,
            ["general.trial_notice_message"] = (message ?? "").Trim(),
        });
        await LogAsync("Update", "Subscription", null, $"Updated trial connector notice (enabled={enabled}, expires {d})", performedBy: "API System");
        TempData["Success"] = "Notice updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel()
    {
        if (RequireManager() is IActionResult deny) return deny;
        await _settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["subscription.active"] = "false",
            ["general.trial_notice_enabled"] = "true", // bring the trial warning back
        });
        await LogAsync("Update", "Subscription", null, "Cancelled API connector subscription", performedBy: "API System");
        TempData["Success"] = "Subscription cancelled — your stores are back on the trial connector.";
        return RedirectToAction(nameof(Index));
    }

    // Save the Zephiel connector settings (surfaced here instead of the generic Settings page).
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveZephiel(bool enabled, string? baseUrl, string? apiSlug, string? accountKey)
    {
        if (RequireManager() is IActionResult deny) return deny;
        var updates = new Dictionary<string, string>
        {
            ["zephiel.enabled"]  = enabled ? "true" : "false",
            ["zephiel.base_url"] = string.IsNullOrWhiteSpace(baseUrl) ? "https://www.zephiel.com" : baseUrl.Trim().TrimEnd('/'),
            ["zephiel.api_slug"] = string.IsNullOrWhiteSpace(apiSlug) ? "multistore" : apiSlug.Trim(),
        };
        // Account key is a secret — encrypt it; blank means keep the existing value.
        if (!string.IsNullOrWhiteSpace(accountKey))
            updates["zephiel.account_key"] = _secrets.Protect(accountKey.Trim());

        await _settings.SaveManyAsync(updates);
        await LogAsync("Update", "Subscription", null, "Updated Zephiel connector settings", performedBy: "API System");
        TempData["Success"] = "Zephiel connector settings saved.";
        return RedirectToAction(nameof(Index));
    }

    // Provision (or refresh) a matching Zephiel API key for every store, so the keys shown here match
    // Zephiel exactly. Idempotent — stores already keyed keep their key.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncKeys()
    {
        if (RequireManager() is IActionResult deny) return deny;
        if (!await _zephiel.IsConfiguredAsync())
        {
            TempData["Error"] = "Turn the Zephiel connector on and set the account key first, then sync.";
            return RedirectToAction(nameof(Index));
        }

        var stores = await _db.Stores.OrderBy(s => s.Name).ToListAsync();
        int provisioned = 0;
        foreach (var s in stores)
            if (await _zephiel.ProvisionStoreKeyAsync(s.Id, s.Name, s.Slug) != null) provisioned++;

        await LogAsync("Update", "Store", "*", $"Synced {provisioned}/{stores.Count} store key(s) to Zephiel", performedBy: "API System");
        TempData["Success"] = $"Zephiel keys are in place for {provisioned} of {stores.Count} store(s).";
        return RedirectToAction(nameof(Index));
    }

    // Force-rebuild every store's key under the CURRENT active Zephiel subscription, overwriting any
    // existing keys. Use when the account's active subscription changed and the stores need re-linking
    // so their traffic (and the live usage chart) lands on the right subscription.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ReconnectStores()
    {
        if (RequireManager() is IActionResult deny) return deny;
        if (!await _zephiel.IsConfiguredAsync())
        {
            TempData["Error"] = "Turn the Zephiel connector on and set the account key first, then reconnect.";
            return RedirectToAction(nameof(Index));
        }

        var stores = await _db.Stores.OrderBy(s => s.Name).ToListAsync();
        int provisioned = 0;
        foreach (var s in stores)
            if (await _zephiel.ProvisionStoreKeyAsync(s.Id, s.Name, s.Slug, force: true) != null) provisioned++;

        await LogAsync("Update", "Store", "*", $"Reconnected {provisioned}/{stores.Count} store(s) to Zephiel with fresh keys", performedBy: "API System");
        TempData["Success"] = $"Reconnected {provisioned} of {stores.Count} store(s) to Zephiel with fresh keys under the current subscription.";
        return RedirectToAction(nameof(Index));
    }
}
