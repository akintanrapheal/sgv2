using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SterlingLams.Web.Services;

/// <summary>One cart/order line in the shape Retainful's abandoned-cart email needs. Flattened into
/// parallel string arrays (line_names, line_prices, …) on the event, per Retainful's sample.</summary>
public sealed record RetainfulLineItem(string Name, decimal Price, int Quantity, string? ImageUrl, string ProductId, string? ProductUrl);

/// <summary>
/// Server-side client for Retainful's REST API (https://api.retainful.net/api/v1). Syncs
/// subscribers and sends the commerce lifecycle events (CHECKOUT_CREATED, ORDER_PLACED, ORDER_PAID,
/// ORDER_FULFILLED, ORDER_CANCELLED, ORDER_REFUNDED) so Retainful's own automations (abandoned cart,
/// post-purchase, win-back) can run. Errors are swallowed — a Retainful hiccup must never affect
/// checkout, sign-up or any customer-facing flow. Config lives in Admin → Integrations (encrypted key).
///
/// Retainful detects an abandoned cart itself from a CHECKOUT_CREATED with no following ORDER_PAID, so
/// while Retainful is enabled the built-in abandoned-cart reminders are held (see AbandonedCartService)
/// to avoid double-emailing. Most call sites go through <see cref="IRetainfulOrderNotifier"/>, which
/// resolves the order's contact/total in one query.
/// </summary>
public interface IRetainfulClient
{
    /// <summary>Add/refresh a newsletter subscriber (opted-in) in Retainful + the configured list.</summary>
    Task SubscribeNewsletterAsync(string email, string? firstName = null, string? lastName = null);

    /// <summary>Fire one commerce lifecycle event to Retainful's /events API. <paramref name="eventName"/>
    /// is the event type Retainful keys automations on (e.g. "CHECKOUT_CREATED", "ORDER_PAID"); Retainful
    /// derives its internal type by uppercasing it. <paramref name="uniqueIdentifier"/> ties a purchase's
    /// whole journey together (same value across checkout → paid → fulfilled), so use the order number —
    /// a checkout that never gets ORDER_PAID is then correctly treated as abandoned.</summary>
    Task SendOrderEventAsync(string eventName, string uniqueIdentifier,
        string? email, string? phone, string? firstName, string? lastName,
        decimal total, int itemCount, string? status = null, string? channel = null,
        IDictionary<string, string>? extra = null,
        IReadOnlyList<RetainfulLineItem>? lines = null, string? recoveryUrl = null, string currency = "NGN");

    /// <summary>Live connection test for the Integrations status panel. Returns (ok, human-readable detail).</summary>
    Task<(bool ok, string detail)> TestAsync();
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

    public async Task SendOrderEventAsync(string eventName, string uniqueIdentifier,
        string? email, string? phone, string? firstName, string? lastName,
        decimal total, int itemCount, string? status = null, string? channel = null,
        IDictionary<string, string>? extra = null,
        IReadOnlyList<RetainfulLineItem>? lines = null, string? recoveryUrl = null, string currency = "NGN")
    {
        if (string.IsNullOrWhiteSpace(eventName) || string.IsNullOrWhiteSpace(uniqueIdentifier)) return;
        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(phone)) return;  // need an identity
        try
        {
            var (on, key, baseUrl, _) = await ConfigAsync();
            if (!on) return;

            // Contact: keep purchasers subscribed so Retainful's flows can reach them (the site already
            // emails customers transactionally; this mirrors that relationship).
            var contact = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(email)) contact["email"] = email!;
            if (!string.IsNullOrWhiteSpace(phone)) contact["phone"] = phone!;
            if (!string.IsNullOrWhiteSpace(firstName)) contact["first_name"] = firstName!;
            if (!string.IsNullOrWhiteSpace(lastName)) contact["last_name"] = lastName!;
            contact["email_opt_in"] = "SUBSCRIBED";

            // eventData follows Retainful's abandoned-cart sample exactly: scalar fields plus PARALLEL
            // string arrays (line_names, line_prices, …) — one entry per cart line, same index across all
            // the line_* arrays. recovery_url is only sent on CHECKOUT_CREATED (the link that rebuilds the
            // customer's checkout). Values are strings or string[] — no nested objects, which Retainful rejects.
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var eventData = new Dictionary<string, object>
            {
                ["currency"] = string.IsNullOrWhiteSpace(currency) ? "NGN" : currency,
                ["cart_total"] = total.ToString("0.##", inv),
                ["item_count"] = itemCount.ToString(),
                ["order_number"] = uniqueIdentifier,
                ["channel"] = string.IsNullOrWhiteSpace(channel) ? "online" : channel!,
            };
            if (!string.IsNullOrWhiteSpace(status)) eventData["status"] = status!;
            if (lines != null && lines.Count > 0)
            {
                eventData["line_names"]        = lines.Select(l => l.Name ?? "").ToArray();
                eventData["line_prices"]       = lines.Select(l => l.Price.ToString("0.##", inv)).ToArray();
                eventData["line_quantities"]   = lines.Select(l => l.Quantity.ToString()).ToArray();
                eventData["line_image_urls"]   = lines.Select(l => l.ImageUrl ?? "").ToArray();
                eventData["line_product_ids"]  = lines.Select(l => l.ProductId ?? "").ToArray();
                eventData["line_product_urls"] = lines.Select(l => l.ProductUrl ?? "").ToArray();
            }
            if (!string.IsNullOrWhiteSpace(recoveryUrl)) eventData["recovery_url"] = recoveryUrl!;
            if (extra != null) foreach (var kv in extra) eventData[kv.Key] = kv.Value;

            await PostAsync(key, $"{baseUrl}/events", new
            {
                eventName,
                uniqueIdentifier,   // same across a purchase's journey → Retainful treats it as one order
                contact,
                eventData,
            });
        }
        catch (Exception ex) { _log.LogWarning(ex, "Retainful event {Event} failed.", eventName); }
    }

    public async Task<(bool ok, string detail)> TestAsync()
    {
        if (!await _settings.GetBoolAsync("retainful.enabled", false))
            return (false, "Not enabled.");
        var key = _secrets.Reveal(await _settings.GetAsync("retainful.api_key", ""));
        if (string.IsNullOrWhiteSpace(key)) return (false, "No API key set.");
        var baseUrl = (await _settings.GetAsync("retainful.base_url", DefaultBase)).Trim().TrimEnd('/');
        if (baseUrl.Length == 0) baseUrl = DefaultBase;
        try
        {
            // Lightweight authenticated GET — lists are the cheapest read and confirm the key + scopes.
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/contact-groups?limit=1");
            req.Headers.TryAddWithoutValidation("X-API-Key", key);
            using var resp = await _http.SendAsync(req);
            if (resp.IsSuccessStatusCode) return (true, "Connected — API key valid.");
            if ((int)resp.StatusCode == 401) return (false, "API key rejected (401). Re-copy the key from Retainful.");
            if ((int)resp.StatusCode == 403) return (false, "Key lacks the Lists/Contacts scope (403).");
            return (false, $"Retainful returned {(int)resp.StatusCode}.");
        }
        catch (Exception ex) { return (false, "Couldn't reach Retainful: " + ex.Message); }
    }
}
