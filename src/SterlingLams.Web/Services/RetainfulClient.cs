using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SterlingLams.Web.Services;

/// <summary>
/// Server-side client for Retainful's REST API (https://api.retainful.net/api/v1). Used to sync
/// customers/subscribers and send order/win-back events so Retainful's own automations can email them.
/// Everything is fire-and-forget and swallows errors — a Retainful hiccup must never affect checkout,
/// sign-up or any customer-facing flow. Config lives in Admin → Integrations (encrypted API key).
///
/// Abandoned-cart reminders are intentionally NOT wired here: the site already has its own
/// (see AbandonedCartService). Wiring Retainful's too would double-email customers.
/// </summary>
public interface IRetainfulClient
{
    /// <summary>Add/refresh a newsletter subscriber (opted-in) in Retainful + the configured list.</summary>
    Task SubscribeNewsletterAsync(string email, string? firstName = null, string? lastName = null);

    /// <summary>Fire a "Placed Order" event (drives win-back / post-purchase automations).</summary>
    Task SendOrderPlacedAsync(string? email, string? phone, string? firstName, string? lastName,
        string orderNumber, decimal total, int itemCount);
}

public class RetainfulClient : IRetainfulClient
{
    private const string DefaultBase = "https://api.retainful.net/api/v1";

    private readonly HttpClient _http;
    private readonly ISettingsService _settings;
    private readonly ISettingsSecretProtector _secrets;
    private readonly ILogger<RetainfulClient> _log;

    public RetainfulClient(HttpClient http, ISettingsService settings,
        ISettingsSecretProtector secrets, ILogger<RetainfulClient> log)
    {
        _http = http;
        _settings = settings;
        _secrets = secrets;
        _log = log;
    }

    private async Task<(bool on, string key, string baseUrl, string listId)> ConfigAsync()
    {
        if (!await _settings.GetBoolAsync("retainful.enabled", false)) return (false, "", "", "");
        var key = _secrets.Reveal(await _settings.GetAsync("retainful.api_key", ""));
        if (string.IsNullOrWhiteSpace(key)) return (false, "", "", "");
        var baseUrl = (await _settings.GetAsync("retainful.base_url", DefaultBase)).Trim().TrimEnd('/');
        if (baseUrl.Length == 0) baseUrl = DefaultBase;
        var listId = (await _settings.GetAsync("retainful.list_id", "")).Trim();
        return (true, key, baseUrl, listId);
    }

    private async Task<bool> PostAsync(string key, string url, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("X-API-Key", key);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            var payload = await resp.Content.ReadAsStringAsync();
            _log.LogWarning("Retainful {Status} on {Url}: {Body}", (int)resp.StatusCode, url,
                payload.Length > 300 ? payload[..300] : payload);
            return false;
        }
        return true;
    }

    public async Task SubscribeNewsletterAsync(string email, string? firstName = null, string? lastName = null)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@')) return;
        try
        {
            var (on, key, baseUrl, listId) = await ConfigAsync();
            if (!on) return;

            // When a list is configured, the add-to-list endpoint upserts the contact AND lists them in
            // one call; otherwise fall back to a plain subscribed-contact upsert.
            if (listId.Length > 0)
            {
                await PostAsync(key, $"{baseUrl}/contact-groups/{listId}/contacts", new
                {
                    email, first_name = firstName ?? "", last_name = lastName ?? "",
                    email_opt_in = "SUBSCRIBED", source = "web-store",
                });
            }
            else
            {
                await PostAsync(key, $"{baseUrl}/customer/create", new
                {
                    email, first_name = firstName ?? "", last_name = lastName ?? "",
                    email_opt_in = "SUBSCRIBED", source = "web-store",
                });
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Retainful newsletter sync failed."); }
    }

    public async Task SendOrderPlacedAsync(string? email, string? phone, string? firstName, string? lastName,
        string orderNumber, decimal total, int itemCount)
    {
        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(phone)) return;  // need an identity
        try
        {
            var (on, key, baseUrl, _) = await ConfigAsync();
            if (!on) return;

            // Contact: keep purchasers subscribed so post-purchase/win-back flows can reach them (the site
            // already emails customers transactionally; this mirrors that relationship).
            var contact = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(email)) contact["email"] = email!;
            if (!string.IsNullOrWhiteSpace(phone)) contact["phone"] = phone!;
            if (!string.IsNullOrWhiteSpace(firstName)) contact["first_name"] = firstName!;
            if (!string.IsNullOrWhiteSpace(lastName)) contact["last_name"] = lastName!;
            contact["email_opt_in"] = "SUBSCRIBED";

            await PostAsync(key, $"{baseUrl}/events", new
            {
                eventName = "Placed Order",
                uniqueIdentifier = orderNumber,   // idempotency key — safe across the return + webhook paths
                contact,
                eventData = new Dictionary<string, string>
                {
                    ["order_number"] = orderNumber,
                    ["order_total"] = total.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                    ["currency"] = "NGN",
                    ["item_count"] = itemCount.ToString(),
                    ["channel"] = "online",
                },
            });
        }
        catch (Exception ex) { _log.LogWarning(ex, "Retainful order event failed."); }
    }
}
