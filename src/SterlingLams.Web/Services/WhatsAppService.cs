using System.Text;

namespace SterlingLams.Web.Services;

/// <summary>
/// Sends WhatsApp messages via a Business API provider. Phase 1 implements Twilio (works with the
/// Twilio Sandbox for testing before a production number/templates exist). The interface is
/// provider-agnostic: swapping to Termii later is a new branch in <see cref="SendAsync"/>, not a
/// change to callers. Credentials are read settings-first (Admin → Integrations, encrypted at rest)
/// with env/config fallback, so entering keys takes effect immediately — no redeploy.
/// </summary>
public interface IWhatsAppService
{
    /// <summary>Sends a plain-text WhatsApp message. Never throws — returns (ok, message) so callers
    /// (order notifications) are never broken by messaging problems. NOTE: outside the 24-hour
    /// customer-initiated window WhatsApp requires a pre-approved template (Phase 2); a plain body
    /// only reaches sandbox testers or within an open session.</summary>
    Task<(bool Ok, string Message)> SendAsync(string toPhone, string body, CancellationToken ct = default);

    /// <summary>True when sending is switched on and the provider credentials are all present.</summary>
    Task<bool> IsConfiguredAsync();
}

public class WhatsAppService : IWhatsAppService
{
    private readonly HttpClient _http;
    private readonly ISettingsService _settings;
    private readonly IConfiguration _config;
    private readonly ILogger<WhatsAppService> _log;

    public WhatsAppService(HttpClient http, ISettingsService settings, IConfiguration config, ILogger<WhatsAppService> log)
    {
        _http = http;
        _settings = settings;
        _config = config;
        _log = log;
    }

    private async Task<string> Get(string key, string? configKey = null)
    {
        var v = await _settings.GetAsync(key, "");
        if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        return (configKey is null ? "" : _config[configKey] ?? "").Trim();
    }

    public async Task<bool> IsConfiguredAsync()
    {
        if (!await _settings.GetBoolAsync("whatsapp.enabled", false)) return false;
        return (await Get("whatsapp.twilio.account_sid", "WhatsApp:Twilio:AccountSid")).Length > 0
            && (await Get("whatsapp.twilio.auth_token",   "WhatsApp:Twilio:AuthToken")).Length > 0
            && (await Get("whatsapp.twilio.from",         "WhatsApp:Twilio:From")).Length > 0;
    }

    /// <summary>Best-effort E.164: keep a leading +, turn a Nigerian 0-prefixed 11-digit local number
    /// into +234…, otherwise assume the caller passed an international number.</summary>
    public static string NormalizePhone(string? phone)
    {
        var s = new string((phone ?? "").Where(c => char.IsDigit(c) || c == '+').ToArray());
        if (s.StartsWith("+")) return s;
        if (s.StartsWith("0") && s.Length == 11) return "+234" + s[1..];   // 08012345678 → +2348012345678
        if (s.StartsWith("234")) return "+" + s;
        return s.Length > 0 ? "+" + s : "";
    }

    public async Task<(bool Ok, string Message)> SendAsync(string toPhone, string body, CancellationToken ct = default)
    {
        try
        {
            if (!await _settings.GetBoolAsync("whatsapp.enabled", false))
                return (false, "WhatsApp sending is turned off (Admin → Integrations → WhatsApp).");

            var provider = (await Get("whatsapp.provider")).ToLowerInvariant();
            if (provider.Length == 0) provider = "twilio";
            if (provider != "twilio")
                return (false, $"WhatsApp provider '{provider}' isn't wired yet — only Twilio is available in Phase 1.");

            var sid   = await Get("whatsapp.twilio.account_sid", "WhatsApp:Twilio:AccountSid");
            var token = await Get("whatsapp.twilio.auth_token",  "WhatsApp:Twilio:AuthToken");
            var from  = await Get("whatsapp.twilio.from",        "WhatsApp:Twilio:From");
            if (sid.Length == 0 || token.Length == 0 || from.Length == 0)
                return (false, "Twilio credentials are incomplete — set Account SID, Auth Token and the From number.");

            var to = NormalizePhone(toPhone);
            if (to.Length < 8)
                return (false, "Enter a valid phone number in international format, e.g. +2348012345678.");

            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"https://api.twilio.com/2010-04-01/Accounts/{sid}/Messages.json");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{sid}:{token}")));
            req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["From"] = "whatsapp:" + NormalizePhone(from),
                ["To"]   = "whatsapp:" + to,
                ["Body"] = body ?? ""
            });

            using var resp = await _http.SendAsync(req, ct);
            var payload = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
                return (true, $"Message queued to {to}.");

            var msg = TryReadTwilioError(payload) ?? $"Twilio returned {(int)resp.StatusCode}.";
            _log.LogWarning("WhatsApp send failed ({Status}): {Payload}", (int)resp.StatusCode, payload);
            return (false, msg);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "WhatsApp send threw");
            return (false, "Could not reach the WhatsApp provider: " + ex.Message);
        }
    }

    // Twilio error bodies are JSON: { "code": 63007, "message": "...", "more_info": "..." }.
    private static string? TryReadTwilioError(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("message", out var m)) return m.GetString();
        }
        catch { /* not JSON */ }
        return null;
    }
}
