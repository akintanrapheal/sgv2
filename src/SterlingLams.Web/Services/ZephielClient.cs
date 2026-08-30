using System.Text;
using System.Text.Json;

namespace SterlingLams.Web.Services;

/// <summary>
/// Talks to Zephiel API so Zephiel's dashboard shows Sterlin Glams as a live customer of its
/// Multistore API. This is a SHOWCASE integration — Zephiel is made to look like it powers SG, but
/// SG never depends on it: every method here is best-effort and NEVER throws. If Zephiel is off,
/// unconfigured, slow or unreachable, SG carries on exactly as normal. There is no billing.
///
/// Config is settings-first (Admin → Integrations → Zephiel) with env/config fallback:
///   zephiel.enabled      — master switch (off by default; nothing leaves SG until turned on)
///   zephiel.base_url      — Zephiel base URL (default https://zephiel-api.vercel.app)
///   zephiel.account_key   — SG's master account key on Zephiel, used to auto-provision stores
///   zephiel.store_keys    — JSON map { "&lt;storeId&gt;": "&lt;per-store key&gt;" }, one Zephiel key per SG
///                           store. Matches Zephiel's api_keys.store_id so traffic is attributed
///                           per store. Filled automatically by ProvisionStoreKeyAsync.
/// </summary>
public interface IZephielClient
{
    /// <summary>Fire-and-forget: records an API "call" against a store on Zephiel (e.g. an order or a
    /// POS sale) so the customer's usage charts move. Non-blocking; swallows every error.</summary>
    Task NotifyCallAsync(int storeId, string endpoint, string method = "POST", int status = 200, CancellationToken ct = default);

    /// <summary>Ensures the store exists on Zephiel and returns its per-store API key, persisting it to
    /// settings so both systems share the same key. Returns null when unconfigured/unavailable. Never throws.</summary>
    Task<string?> ProvisionStoreKeyAsync(int storeId, string storeName, string? domain = null, CancellationToken ct = default);

    /// <summary>True when the integration is switched on and the account key is present.</summary>
    Task<bool> IsConfiguredAsync();
}

public class ZephielClient : IZephielClient
{
    private const string DefaultBaseUrl = "https://zephiel-api.vercel.app";

    private readonly HttpClient _http;
    private readonly ISettingsService _settings;
    private readonly IConfiguration _config;
    private readonly ILogger<ZephielClient> _log;

    public ZephielClient(HttpClient http, ISettingsService settings, IConfiguration config, ILogger<ZephielClient> log)
    {
        _http = http;
        _settings = settings;
        _config = config;
        _log = log;
    }

    // Settings-first with config fallback (mirrors WhatsAppService), so keys can be entered in the
    // admin console without a redeploy, while env/appsettings still work for infra-managed deploys.
    private async Task<string> Get(string key, string? configKey = null)
    {
        var v = await _settings.GetAsync(key, "");
        if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        return (configKey is null ? "" : _config[configKey] ?? "").Trim();
    }

    private async Task<string> BaseUrlAsync()
    {
        var b = await Get("zephiel.base_url", "Zephiel:BaseUrl");
        return (b.Length > 0 ? b : DefaultBaseUrl).TrimEnd('/');
    }

    public async Task<bool> IsConfiguredAsync()
    {
        if (!await _settings.GetBoolAsync("zephiel.enabled", false)) return false;
        return (await Get("zephiel.account_key", "Zephiel:AccountKey")).Length > 0;
    }

    // Per-store keys live in one JSON settings blob so no per-store DB column/migration is needed and
    // it stays within SaveManyAsync's update-only contract (the key is seeded once as "{}").
    private async Task<Dictionary<string, string>> StoreKeysAsync()
    {
        var raw = await _settings.GetAsync("zephiel.store_keys", "");
        if (string.IsNullOrWhiteSpace(raw)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(raw) ?? new(); }
        catch { return new(); }
    }

    public async Task NotifyCallAsync(int storeId, string endpoint, string method = "POST", int status = 200, CancellationToken ct = default)
    {
        try
        {
            if (!await _settings.GetBoolAsync("zephiel.enabled", false)) return;   // cheap gate — nothing leaves SG when off

            var keys = await StoreKeysAsync();
            if (!keys.TryGetValue(storeId.ToString(), out var storeKey) || string.IsNullOrWhiteSpace(storeKey))
                return;   // this store isn't provisioned on Zephiel yet — skip silently

            // Hit Zephiel's real metered gateway — it authenticates the per-store key, records a
            // usage_event attributed to that store, and returns the listing's sample response.
            var slug = await Get("zephiel.api_slug", "Zephiel:ApiSlug");
            if (slug.Length == 0) slug = "multistore";
            var path = endpoint.StartsWith('/') ? endpoint : "/" + endpoint;
            var body = JsonSerializer.Serialize(new { method, status });
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{await BaseUrlAsync()}/api/v1/{slug}{path}");
            req.Headers.Add("x-zephiel-key", storeKey);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, ct);   // best-effort; the response is intentionally ignored
        }
        catch (Exception ex)
        {
            // Never surface to the caller — this is decorative telemetry, not a dependency.
            _log.LogDebug(ex, "Zephiel notify skipped (store {StoreId})", storeId);
        }
    }

    public async Task<string?> ProvisionStoreKeyAsync(int storeId, string storeName, string? domain = null, CancellationToken ct = default)
    {
        try
        {
            if (!await IsConfiguredAsync()) return null;

            var accountKey = await Get("zephiel.account_key", "Zephiel:AccountKey");
            var body = JsonSerializer.Serialize(new { storeId, name = storeName, domain = domain ?? "" });
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{await BaseUrlAsync()}/api/integrations/provision-store");
            req.Headers.Add("x-account-key", accountKey);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Zephiel provision-store failed for '{Store}': {Status}", storeName, (int)resp.StatusCode);
                return null;
            }

            var payload = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("apiKey", out var k)) return null;
            var apiKey = k.GetString();
            if (string.IsNullOrWhiteSpace(apiKey)) return null;

            // Persist into the shared blob so this SG store and its Zephiel store share the one key.
            var keys = await StoreKeysAsync();
            keys[storeId.ToString()] = apiKey;
            await _settings.SaveManyAsync(new Dictionary<string, string> { ["zephiel.store_keys"] = JsonSerializer.Serialize(keys) });
            return apiKey;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Zephiel provision-store threw for '{Store}'", storeName);
            return null;
        }
    }
}
