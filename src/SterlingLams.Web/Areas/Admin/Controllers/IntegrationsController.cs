using Microsoft.AspNetCore.Mvc;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers;

/// <summary>
/// Payment gateway keys, webhook config and SMTP credentials — the sensitive integration settings.
/// <c>Section => null</c> makes <see cref="AdminBaseController"/> restrict this to full administrators
/// only (no staff role can reach it), honouring "only me should have access to it". Secret values are
/// encrypted at rest and never rendered back to the browser.
/// </summary>
public class IntegrationsController : AdminBaseController
{
    // Section == null → AdminBaseController allows full administrators (Admin/Owner/Developer) only.
    // A previously granted "Integrations" permission can no longer open this screen either.
    protected override string? Section => null;

    private static readonly string[] Groups = { "Payments", "SMTP", "WhatsApp", "PostHog", "Google Analytics", "Meta Pixel", "Cloudinary", "Retainful", "ImageKit" };
    private static readonly string[] Providers = { "paystack", "stripe", "flutterwave" };

    private readonly ISettingsService _settings;
    private readonly ISettingsSecretProtector _secrets;
    private readonly IConfiguration _config;
    private readonly IWhatsAppService _whatsapp;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IGoogleAnalytics _ga;
    private readonly IRetainfulClient _retainful;

    public IntegrationsController(ISettingsService settings, ISettingsSecretProtector secrets,
        IConfiguration config, IWhatsAppService whatsapp, IHttpClientFactory httpFactory,
        IGoogleAnalytics ga, IRetainfulClient retainful)
    {
        _settings = settings;
        _secrets = secrets;
        _config = config;
        _whatsapp = whatsapp;
        _httpFactory = httpFactory;
        _ga = ga;
        _retainful = retainful;
    }

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Integrations";

        // Raw DB values (secrets stay encrypted here — we only need to know if they're set).
        var raw = (await _settings.GetAllAsync())
            .Where(s => Groups.Contains(s.Group))
            .ToDictionary(s => s.Key, s => s.Value ?? "");

        // A secret is "configured" if it's stored in the DB or supplied via appsettings/env.
        bool Set(string key, string? configKey) =>
            !string.IsNullOrWhiteSpace(raw.GetValueOrDefault(key))
            || !string.IsNullOrWhiteSpace(configKey is null ? null : _config[configKey]);

        string Plain(string key, string? configKey) =>
            raw.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
                ? _secrets.Reveal(v)
                : (configKey is null ? "" : _config[configKey] ?? "");

        var baseUrl = (_config["App:BaseUrl"] ?? "").TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
            baseUrl = $"{Request.Scheme}://{Request.Host}";

        var vm = new IntegrationsViewModel
        {
            Provider              = (await _settings.GetAsync("payment.provider", _config["Payment:Provider"] ?? "paystack")).ToLowerInvariant(),
            PaystackPublicKey     = Plain("payment.paystack.public_key", "Payment:Paystack:PublicKey"),
            PaystackSecretSet     = Set("payment.paystack.secret_key", "Payment:Paystack:SecretKey"),
            StripePublishableKey  = Plain("payment.stripe.publishable_key", "Payment:Stripe:PublishableKey"),
            StripeSecretSet       = Set("payment.stripe.secret_key", "Payment:Stripe:SecretKey"),
            StripeWebhookSet      = Set("payment.stripe.webhook_secret", "Payment:Stripe:WebhookSecret"),
            FlutterwavePublicKey  = Plain("payment.flutterwave.public_key", "Payment:Flutterwave:PublicKey"),
            FlutterwaveSecretSet  = Set("payment.flutterwave.secret_key", "Payment:Flutterwave:SecretKey"),
            FlutterwaveEncSet     = Set("payment.flutterwave.encryption_key", "Payment:Flutterwave:EncryptionKey"),

            SmtpEnabled     = await _settings.GetBoolAsync("email.smtp.enabled", false),
            SmtpHost        = Plain("email.smtp.host", "Email:Host"),
            SmtpPort        = await _settings.GetIntAsync("email.smtp.port", 587),
            SmtpUsername    = Plain("email.smtp.username", "Email:Username"),
            SmtpPasswordSet = Set("email.smtp.password", "Email:Password"),
            SmtpFromAddress = Plain("email.smtp.from_address", "Email:FromAddress"),
            SmtpFromName    = string.IsNullOrWhiteSpace(Plain("email.smtp.from_name", null)) ? "Sterlin Glams" : Plain("email.smtp.from_name", null),
            SmtpSsl         = await _settings.GetBoolAsync("email.smtp.ssl", true),

            WaEnabled       = await _settings.GetBoolAsync("whatsapp.enabled", false),
            WaProvider      = string.IsNullOrWhiteSpace(Plain("whatsapp.provider", null)) ? "twilio" : Plain("whatsapp.provider", null),
            WaAccountSid    = Plain("whatsapp.twilio.account_sid", "WhatsApp:Twilio:AccountSid"),
            WaAuthTokenSet  = Set("whatsapp.twilio.auth_token", "WhatsApp:Twilio:AuthToken"),
            WaFrom          = Plain("whatsapp.twilio.from", "WhatsApp:Twilio:From"),
            WaNotifyOrderConfirmed  = await _settings.GetBoolAsync("whatsapp.notify.order_confirmed", false),
            WaNotifyPaymentReceived = await _settings.GetBoolAsync("whatsapp.notify.payment_received", false),
            WaNotifyReadyForPickup  = await _settings.GetBoolAsync("whatsapp.notify.ready_for_pickup", false),
            WaNotifyShipped         = await _settings.GetBoolAsync("whatsapp.notify.shipped", false),
            WaNotifyDelivered       = await _settings.GetBoolAsync("whatsapp.notify.delivered", false),
            WaTplOrderConfirmed  = Plain("whatsapp.template.order_confirmed", null),
            WaTplPaymentReceived = Plain("whatsapp.template.payment_received", null),
            WaTplReadyForPickup  = Plain("whatsapp.template.ready_for_pickup", null),
            WaTplShipped         = Plain("whatsapp.template.shipped", null),
            WaTplDelivered       = Plain("whatsapp.template.delivered", null),

            PhEnabled    = await _settings.GetBoolAsync("posthog.enabled", false),
            PhProjectKey = Plain("posthog.project_api_key", null),
            PhRegion     = string.IsNullOrWhiteSpace(Plain("posthog.region", null)) ? "eu" : Plain("posthog.region", null).ToLowerInvariant(),

            GaEnabled           = await _settings.GetBoolAsync("ga.enabled", false),
            GaMeasurementId     = Plain("ga.measurement_id", null),
            GaAdsConversionId   = Plain("ga.ads_conversion_id", null),
            GaPropertyId        = Plain("ga.property_id", null),
            GaServiceAccountSet = Set("ga.service_account_json", null),

            MetaEnabled            = await _settings.GetBoolAsync("meta.enabled", false),
            MetaPixelId            = Plain("meta.pixel_id", null),
            MetaDomainVerification = Plain("meta.domain_verification", null),

            CloudinaryCloudName  = Plain("cloudinary.cloud_name", "Cloudinary:CloudName"),
            CloudinaryApiKeySet  = Set("cloudinary.api_key", "Cloudinary:ApiKey"),
            CloudinaryApiSecretSet = Set("cloudinary.api_secret", "Cloudinary:ApiSecret"),

            RetainfulEnabled   = await _settings.GetBoolAsync("retainful.enabled", false),
            RetainfulApiKeySet = Set("retainful.api_key", null),
            RetainfulBaseUrl   = string.IsNullOrWhiteSpace(Plain("retainful.base_url", null)) ? "https://api.retainful.net/api/v1" : Plain("retainful.base_url", null),
            RetainfulListId    = Plain("retainful.list_id", null),
            RetainfulHandleAbandonedCart = await _settings.GetBoolAsync("retainful.handle_abandoned_cart", false),

            ImageKitEnabled     = await _settings.GetBoolAsync("imagekit.enabled", false),
            ImageKitEndpoint    = Plain("imagekit.url_endpoint", null),
            ImageKitPublicKey   = Plain("imagekit.public_key", null),
            ImageKitPrivateSet  = Set("imagekit.private_key", null),

            BaseUrl = baseUrl,
        };
        return View(vm);
    }

    /// <summary>Sends a test WhatsApp message to confirm the credentials work (Twilio Sandbox: the
    /// recipient must have joined the sandbox first). Returns JSON for the inline test button.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> TestWhatsApp(string toPhone)
    {
        if (string.IsNullOrWhiteSpace(toPhone))
            return Json(new { ok = false, message = "Enter a phone number to send the test to." });

        var (ok, message) = await _whatsapp.SendAsync(toPhone,
            "✅ Test from Sterlin Glams — your WhatsApp integration is working.");
        await LogAsync("Update", "Setting", null, $"Sent test WhatsApp to {toPhone}: {(ok ? "ok" : "failed")}");
        return Json(new { ok, message });
    }

    /// <summary>Live connection test for one integration, for the status panel. Returns
    /// { ok, status: "ok"|"error"|"off", detail }. Non-destructive (read-only pings) except none send
    /// anything. Client-side integrations (Meta Pixel, PostHog) report config state only.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> TestConnection(string name)
    {
        // Reveal a stored secret, falling back to appsettings/env (same precedence as the page).
        async Task<string> Val(string key, string? configKey)
        {
            var raw = await _settings.GetAsync(key, "");
            if (!string.IsNullOrWhiteSpace(raw)) return _secrets.Reveal(raw);
            return configKey is null ? "" : (_config[configKey] ?? "");
        }
        IActionResult R(bool ok, string detail, string? status = null)
            => Json(new { ok, status = status ?? (ok ? "ok" : "error"), detail });

        try
        {
            switch ((name ?? "").ToLowerInvariant())
            {
                case "retainful":
                {
                    var (ok, detail) = await _retainful.TestAsync();
                    return R(ok, detail, ok ? "ok" : (detail.Contains("Not enabled") || detail.Contains("No API key") ? "off" : "error"));
                }

                case "google analytics":
                {
                    var r = await _ga.GetAsync(7);
                    if (!r.Configured) return R(false, "Not set up (add Property ID + service-account key).", "off");
                    if (!string.IsNullOrEmpty(r.Error)) return R(false, r.Error, "error");
                    return R(true, $"Connected — {r.Stats?.Users ?? 0} users in the last 7 days.");
                }

                case "cloudinary":
                {
                    var cloud = await Val("cloudinary.cloud_name", "Cloudinary:CloudName");
                    var k = await Val("cloudinary.api_key", "Cloudinary:ApiKey");
                    var s = await Val("cloudinary.api_secret", "Cloudinary:ApiSecret");
                    if (cloud.Length == 0 || k.Length == 0 || s.Length == 0) return R(false, "Cloud name, API key and secret are required.", "off");
                    using var http = _httpFactory.CreateClient(); http.Timeout = TimeSpan.FromSeconds(12);
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.cloudinary.com/v1_1/{cloud}/ping");
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                        Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{k}:{s}")));
                    using var resp = await http.SendAsync(req);
                    return resp.IsSuccessStatusCode
                        ? R(true, "Connected — credentials valid.")
                        : R(false, (int)resp.StatusCode == 401 ? "API key/secret rejected (401)." : $"Cloudinary returned {(int)resp.StatusCode}.");
                }

                case "payments":
                {
                    var provider = (await _settings.GetAsync("payment.provider", _config["Payment:Provider"] ?? "paystack")).ToLowerInvariant();
                    using var http = _httpFactory.CreateClient(); http.Timeout = TimeSpan.FromSeconds(12);
                    if (provider == "paystack")
                    {
                        var sk = await Val("payment.paystack.secret_key", "Payment:Paystack:SecretKey");
                        if (sk.Length == 0) return R(false, "No Paystack secret key.", "off");
                        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.paystack.co/bank?perPage=1");
                        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sk);
                        using var resp = await http.SendAsync(req);
                        return resp.IsSuccessStatusCode ? R(true, "Connected — Paystack secret key valid.")
                            : R(false, (int)resp.StatusCode == 401 ? "Paystack secret key rejected (401)." : $"Paystack returned {(int)resp.StatusCode}.");
                    }
                    if (provider == "stripe")
                    {
                        var sk = await Val("payment.stripe.secret_key", "Payment:Stripe:SecretKey");
                        if (sk.Length == 0) return R(false, "No Stripe secret key.", "off");
                        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.stripe.com/v1/balance");
                        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sk);
                        using var resp = await http.SendAsync(req);
                        return resp.IsSuccessStatusCode ? R(true, "Connected — Stripe secret key valid.")
                            : R(false, (int)resp.StatusCode == 401 ? "Stripe secret key rejected (401)." : $"Stripe returned {(int)resp.StatusCode}.");
                    }
                    var fk = await Val("payment.flutterwave.secret_key", "Payment:Flutterwave:SecretKey");
                    return fk.Length > 0 ? R(true, "Flutterwave secret key is set (live verification not supported here).", "ok")
                        : R(false, "No Flutterwave secret key.", "off");
                }

                case "whatsapp":
                {
                    if (!await _settings.GetBoolAsync("whatsapp.enabled", false)) return R(false, "Not enabled.", "off");
                    var sid = await Val("whatsapp.twilio.account_sid", "WhatsApp:Twilio:AccountSid");
                    var tok = await Val("whatsapp.twilio.auth_token", "WhatsApp:Twilio:AuthToken");
                    if (sid.Length == 0 || tok.Length == 0) return R(false, "Twilio Account SID + Auth Token required.", "off");
                    using var http = _httpFactory.CreateClient(); http.Timeout = TimeSpan.FromSeconds(12);
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.twilio.com/2010-04-01/Accounts/{sid}.json");
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                        Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{sid}:{tok}")));
                    using var resp = await http.SendAsync(req);
                    return resp.IsSuccessStatusCode ? R(true, "Connected — Twilio credentials valid.")
                        : R(false, (int)resp.StatusCode == 401 ? "Twilio SID/token rejected (401)." : $"Twilio returned {(int)resp.StatusCode}.");
                }

                case "smtp":
                {
                    if (!await _settings.GetBoolAsync("email.smtp.enabled", false)) return R(false, "Not enabled.", "off");
                    var host = await Val("email.smtp.host", "Email:Host");
                    var port = await _settings.GetIntAsync("email.smtp.port", 587);
                    if (host.Length == 0) return R(false, "No SMTP host set.", "off");
                    try
                    {
                        using var tcp = new System.Net.Sockets.TcpClient();
                        var connect = tcp.ConnectAsync(host, port);
                        if (await Task.WhenAny(connect, Task.Delay(8000)) != connect || !tcp.Connected)
                            return R(false, $"Couldn't reach {host}:{port} (timed out).");
                        return R(true, $"Reachable — {host}:{port} accepts connections. Use ‘Send a test’ below to confirm login.", "ok");
                    }
                    catch (Exception ex) { return R(false, $"Couldn't reach {host}:{port}: {ex.Message}"); }
                }

                case "meta pixel":
                {
                    var on = await _settings.GetBoolAsync("meta.enabled", false);
                    var id = await Val("meta.pixel_id", null);
                    if (!on || id.Length == 0) return R(false, "Not set up.", "off");
                    return R(true, $"Pixel {id} is live on the storefront (client-side — can’t be pinged from here).", "ok");
                }

                case "posthog":
                {
                    var on = await _settings.GetBoolAsync("posthog.enabled", false);
                    var k = await Val("posthog.project_api_key", null);
                    if (!on || k.Length == 0) return R(false, "Not set up.", "off");
                    return R(true, "Enabled on the storefront (client-side — can’t be pinged from here).", "ok");
                }

                case "imagekit":
                {
                    var on = await _settings.GetBoolAsync("imagekit.enabled", false);
                    var ep = (await Val("imagekit.url_endpoint", null)).TrimEnd('/');
                    if (ep.Length == 0) return R(false, "No URL-endpoint set.", "off");
                    // Fetch a tiny transformed image through the endpoint — proves the endpoint + its origin work.
                    using var http = _httpFactory.CreateClient(); http.Timeout = TimeSpan.FromSeconds(12);
                    using var resp = await http.GetAsync($"{ep}/tr:w-1,h-1/");
                    if (resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                        // 200 = reachable; 404 = endpoint live but that path has no image — endpoint itself is valid.
                        return R(true, on ? "Connected — ImageKit endpoint reachable (delivery is ON)." : "Reachable — endpoint valid (delivery is currently OFF).", on ? "ok" : "off");
                    return R(false, $"ImageKit endpoint returned {(int)resp.StatusCode}.");
                }

                default:
                    return R(false, "Unknown integration.", "error");
            }
        }
        catch (Exception ex)
        {
            return R(false, "Test failed: " + ex.Message, "error");
        }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(IFormCollection form)
    {
        var defs = (await _settings.GetAllAsync()).Where(s => Groups.Contains(s.Group)).ToList();
        var updates = new Dictionary<string, string>();
        int secretsUpdated = 0;

        foreach (var s in defs)
        {
            if (s.Key == "payment.provider")
            {
                var p = (form["payment.provider"].ToString() ?? "").Trim().ToLowerInvariant();
                updates[s.Key] = Providers.Contains(p) ? p : "paystack";
            }
            else if (s.Type == "boolean")
            {
                updates[s.Key] = form.ContainsKey(s.Key) ? "true" : "false";
            }
            else if (s.Type == "secret")
            {
                // Secret fields render blank (never echoed). A blank submit means "keep current";
                // a non-blank submit replaces the stored secret with a freshly encrypted value.
                var entered = form[s.Key].ToString();
                if (!string.IsNullOrWhiteSpace(entered))
                {
                    updates[s.Key] = _secrets.Protect(entered.Trim());
                    secretsUpdated++;
                }
            }
            else if (form.ContainsKey(s.Key))
            {
                updates[s.Key] = form[s.Key].ToString().Trim();
            }
        }

        await _settings.SaveManyAsync(updates);

        // Apply the image-delivery switch immediately (no redeploy) so enabling/disabling ImageKit or
        // changing its endpoint takes effect on the next image rendered.
        SterlingLams.Web.Infrastructure.Img.ConfigureDelivery(
            await _settings.GetBoolAsync("imagekit.enabled", false),
            await _settings.GetAsync("imagekit.url_endpoint", ""));

        await LogAsync("Update", "Setting", null,
            $"Updated Integrations settings ({updates.Count} fields, {secretsUpdated} secret(s) changed)");
        TempData["Success"] = "Integration settings saved.";
        return RedirectToAction(nameof(Index));
    }
}

public class IntegrationsViewModel
{
    public string Provider { get; set; } = "paystack";
    public string PaystackPublicKey { get; set; } = "";
    public bool PaystackSecretSet { get; set; }
    public string StripePublishableKey { get; set; } = "";
    public bool StripeSecretSet { get; set; }
    public bool StripeWebhookSet { get; set; }
    public string FlutterwavePublicKey { get; set; } = "";
    public bool FlutterwaveSecretSet { get; set; }
    public bool FlutterwaveEncSet { get; set; }

    public bool SmtpEnabled { get; set; }
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public string SmtpUsername { get; set; } = "";
    public bool SmtpPasswordSet { get; set; }
    public string SmtpFromAddress { get; set; } = "";
    public string SmtpFromName { get; set; } = "Sterlin Glams";
    public bool SmtpSsl { get; set; } = true;

    // WhatsApp
    public bool WaEnabled { get; set; }
    public string WaProvider { get; set; } = "twilio";
    public string WaAccountSid { get; set; } = "";
    public bool WaAuthTokenSet { get; set; }
    public string WaFrom { get; set; } = "";
    public bool WaNotifyOrderConfirmed { get; set; }
    public bool WaNotifyPaymentReceived { get; set; }
    public bool WaNotifyReadyForPickup { get; set; }
    public bool WaNotifyShipped { get; set; }
    public bool WaNotifyDelivered { get; set; }
    public string WaTplOrderConfirmed { get; set; } = "";
    public string WaTplPaymentReceived { get; set; } = "";
    public string WaTplReadyForPickup { get; set; } = "";
    public string WaTplShipped { get; set; } = "";
    public string WaTplDelivered { get; set; } = "";

    // PostHog (product analytics). Project key is public by design, so it's shown in full.
    public bool PhEnabled { get; set; }
    public string PhProjectKey { get; set; } = "";
    public string PhRegion { get; set; } = "eu";

    // Google Analytics 4. Measurement ID is public (ships in the page); the service-account key is secret.
    public bool GaEnabled { get; set; }
    public string GaMeasurementId { get; set; } = "";
    public string GaAdsConversionId { get; set; } = "";
    public string GaPropertyId { get; set; } = "";

    // Meta Pixel (Facebook/Instagram ads). Pixel id + verification code are public (ship in the page).
    public bool MetaEnabled { get; set; }
    public string MetaPixelId { get; set; } = "";
    public string MetaDomainVerification { get; set; } = "";
    public bool GaServiceAccountSet { get; set; }

    // Cloudinary (image hosting). Cloud name is not secret; the API key + secret are.
    public string CloudinaryCloudName { get; set; } = "";
    public bool CloudinaryApiKeySet { get; set; }
    public bool CloudinaryApiSecretSet { get; set; }

    // Retainful (email marketing / win-back). API key is secret.
    public bool RetainfulEnabled { get; set; }
    public bool RetainfulApiKeySet { get; set; }
    public string RetainfulBaseUrl { get; set; } = "https://api.retainful.net/api/v1";
    public string RetainfulListId { get; set; } = "";
    public bool RetainfulHandleAbandonedCart { get; set; }

    // ImageKit (image hosting / CDN) — private key is secret.
    public bool ImageKitEnabled { get; set; }
    public string ImageKitEndpoint { get; set; } = "";
    public string ImageKitPublicKey { get; set; } = "";
    public bool ImageKitPrivateSet { get; set; }

    public string BaseUrl { get; set; } = "";
}
