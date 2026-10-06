using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services.Marketing;

/// <summary>
/// Sends the Purchase conversion to Meta from the server (the Conversions API), alongside the browser
/// pixel in _MetaPixelSnippet/Confirmation.
///
/// Why both: the browser event is lost whenever an ad blocker, tracking protection or ITP stops
/// fbevents.js — on this storefront's traffic that is a large minority of sales. The server event leaves
/// our own machine, so it arrives regardless. Both carry the same <see cref="MetaCapi.PurchaseEventId"/>,
/// which is how Meta recognises them as one conversion and counts it once rather than twice.
///
/// Everything here is best-effort: a failure to report a sale must never affect the customer's
/// confirmation page, so nothing throws and nothing blocks.
/// </summary>
public interface IMetaConversionsApi
{
    /// <summary>Reports <paramref name="order"/> as a Purchase. Never throws.</summary>
    Task SendPurchaseAsync(Order order, HttpContext http, CancellationToken ct = default);

    /// <summary>True when a pixel id and an access token are both configured and CAPI is switched on.</summary>
    Task<bool> IsConfiguredAsync();

    /// <summary>
    /// Asks Meta whether the saved token can actually read the saved dataset, so a wrong or expired
    /// token is caught on the Integrations page rather than by silently unreported sales. Read-only —
    /// it records no event.
    /// </summary>
    Task<(bool Ok, string Message)> ValidateAsync(CancellationToken ct = default);
}

public sealed class MetaConversionsApi : IMetaConversionsApi
{
    // Pinned rather than floating: Meta's Graph versions expire on a published schedule, and a silent
    // bump could change field handling under us. Bump deliberately when upgrading.
    private const string GraphVersion = "v21.0";

    private readonly HttpClient _http;
    private readonly ISettingsService _settings;
    private readonly ISettingsSecretProtector _secrets;
    private readonly ILogger<MetaConversionsApi> _logger;

    public MetaConversionsApi(HttpClient http, ISettingsService settings,
        ISettingsSecretProtector secrets, ILogger<MetaConversionsApi> logger)
    {
        _http = http;
        _settings = settings;
        _secrets = secrets;
        _logger = logger;
    }

    private async Task<(bool On, string PixelId, string Token, string TestCode)> ConfigAsync()
    {
        var on = await _settings.GetBoolAsync("meta.capi_enabled", false);
        var pixel = (await _settings.GetAsync("meta.pixel_id", "")).Trim();
        var token = _secrets.Reveal(await _settings.GetAsync("meta.capi_token", "")).Trim();
        var test = (await _settings.GetAsync("meta.capi_test_code", "")).Trim();
        return (on, pixel, token, test);
    }

    public async Task<bool> IsConfiguredAsync()
    {
        var (on, pixel, token, _) = await ConfigAsync();
        return on && MetaCapi.LooksLikePixelId(pixel) && token.Length > 0;
    }

    public async Task<(bool Ok, string Message)> ValidateAsync(CancellationToken ct = default)
    {
        var (on, pixel, token, testCode) = await ConfigAsync();
        if (!MetaCapi.LooksLikePixelId(pixel)) return (false, "No valid Pixel ID saved (numbers only).");
        if (token.Length == 0) return (false, "No Conversions API access token saved.");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{GraphVersion}/{pixel}?fields=id,name");
            // Bearer header rather than ?access_token= so the token never appears in a URL.
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return (false, $"Meta rejected the token ({(int)resp.StatusCode}): {Truncate(body, 200)}");

            var name = "";
            try { name = JsonDocument.Parse(body).RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""; }
            catch { /* a 200 is enough; the friendly name is a bonus */ }

            return (true, on
                ? $"Token is valid for dataset {pixel}{(name.Length > 0 ? $" ({name})" : "")}. Server-side purchases are ON."
                  + (testCode.Length > 0 ? $" ⚠ Test code {testCode} is set — real sales are going to the TEST stream, not your reports." : "")
                : $"Token is valid for dataset {pixel}{(name.Length > 0 ? $" ({name})" : "")}, but server-side purchases are switched OFF.");
        }
        catch (Exception ex)
        {
            return (false, $"Couldn't reach Meta: {ex.Message}");
        }
    }

    public async Task SendPurchaseAsync(Order order, HttpContext http, CancellationToken ct = default)
    {
        try
        {
            var (on, pixel, token, testCode) = await ConfigAsync();
            if (!on || !MetaCapi.LooksLikePixelId(pixel) || token.Length == 0) return;

            var payload = BuildPurchase(order, http, testCode);
            var json = JsonSerializer.Serialize(payload);

            // The token goes in the body, not the query string, so it can't be captured by proxy or
            // request logging that records URLs.
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{GraphVersion}/{pixel}/events") { Content = content };

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                // Meta returns a JSON error describing what it rejected. It echoes no access token and
                // no raw PII (we only ever send hashes), so it is safe to log and is the only way to
                // diagnose a rejected event.
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Meta CAPI purchase for {OrderNumber} rejected ({Status}): {Body}",
                    order.OrderNumber, (int)resp.StatusCode, Truncate(body, 500));
            }
        }
        catch (Exception ex)
        {
            // Reporting a sale must never cost the customer their confirmation page.
            _logger.LogWarning(ex, "Meta CAPI purchase for {OrderNumber} could not be sent.", order.OrderNumber);
        }
    }

    /// <summary>The request body for one Purchase event. Separated out so it can be asserted in tests.</summary>
    public static object BuildPurchase(Order order, HttpContext http, string testCode)
    {
        var addr = order.DeliveryAddress;
        var email = order.User?.Email;
        var phone = addr?.Phone;

        // Name: the delivery address is what the customer actually typed for this order, so prefer it
        // over the account's name, which may be stale or a placeholder.
        var (first, last) = MetaCapi.SplitName(addr?.FullName, order.User?.FirstName, order.User?.LastName);

        // Hashed identifiers. Meta matches on whichever are present, so send every one we hold —
        // each additional field measurably raises the match rate.
        var user = new Dictionary<string, object?>();
        void Hashed(string key, string? value)
        {
            var h = MetaCapi.Hash(value);
            if (h != null) user[key] = new[] { h };
        }
        Hashed("em", MetaCapi.NormalizeEmail(email));
        Hashed("ph", MetaCapi.NormalizePhone(phone));
        Hashed("fn", MetaCapi.NormalizeName(first));
        Hashed("ln", MetaCapi.NormalizeName(last));
        Hashed("ct", MetaCapi.NormalizePlace(addr?.City));
        Hashed("st", MetaCapi.NormalizePlace(addr?.State));
        Hashed("zp", MetaCapi.NormalizePlace(addr?.PostalCode));
        Hashed("country", MetaCapi.NormalizeCountry(addr?.Country));
        if (!string.IsNullOrWhiteSpace(order.UserId))
        {
            var h = MetaCapi.Hash(order.UserId.Trim().ToLowerInvariant());
            if (h != null) user["external_id"] = new[] { h };
        }

        // Sent in the clear on purpose — Meta requires these unhashed, and they are the strongest
        // signals for tying the sale back to the ad click that produced it.
        var ip = http.Connection.RemoteIpAddress?.ToString();
        if (!string.IsNullOrWhiteSpace(ip)) user["client_ip_address"] = ip;
        var ua = http.Request.Headers.UserAgent.ToString();
        if (!string.IsNullOrWhiteSpace(ua)) user["client_user_agent"] = ua;
        var fbp = http.Request.Cookies["_fbp"];
        if (!string.IsNullOrWhiteSpace(fbp)) user["fbp"] = fbp;
        var fbc = http.Request.Cookies["_fbc"];
        if (!string.IsNullOrWhiteSpace(fbc)) user["fbc"] = fbc;

        var items = (order.Items ?? new List<OrderItem>()).Select(i => new Dictionary<string, object?>
        {
            ["id"] = i.ProductId.ToString(CultureInfo.InvariantCulture),
            ["quantity"] = i.Quantity,
            ["item_price"] = decimal.ToDouble(i.UnitPrice)
        }).ToList();

        var custom = new Dictionary<string, object?>
        {
            ["currency"] = string.IsNullOrWhiteSpace(order.Currency) ? "NGN" : order.Currency,
            ["value"] = decimal.ToDouble(order.Total),
            ["content_type"] = "product",
            ["contents"] = items,
            ["content_ids"] = items.Select(i => i["id"]).Distinct().ToList(),
            ["num_items"] = (order.Items ?? new List<OrderItem>()).Sum(i => i.Quantity),
            ["order_id"] = order.OrderNumber
        };

        var ev = new Dictionary<string, object?>
        {
            ["event_name"] = "Purchase",
            // When the money landed, not when this page was rendered — a refresh days later must not
            // re-date the sale. Meta rejects events older than 7 days.
            ["event_time"] = new DateTimeOffset(DateTime.SpecifyKind(
                order.PaidAt ?? order.CreatedAt, DateTimeKind.Utc)).ToUnixTimeSeconds(),
            ["event_id"] = MetaCapi.PurchaseEventId(order.OrderNumber),
            ["event_source_url"] = $"{http.Request.Scheme}://{http.Request.Host}{http.Request.Path}",
            ["action_source"] = "website",
            ["user_data"] = user,
            ["custom_data"] = custom
        };

        var body = new Dictionary<string, object?> { ["data"] = new[] { ev } };
        // Only set while someone is watching Events Manager → Test Events; leaving it on would keep
        // real sales in the test stream instead of reporting them.
        if (!string.IsNullOrWhiteSpace(testCode)) body["test_event_code"] = testCode;
        return body;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>
/// Normalisation + hashing for Meta's Conversions API. Meta hashes the same values on its side and
/// compares the digests, so a value normalised differently from their rules simply fails to match and
/// the conversion goes unattributed — hence the exact lower-casing/stripping below.
/// </summary>
public static class MetaCapi
{
    /// <summary>
    /// The id shared by the browser pixel and the server event for one sale. Both must send it, or Meta
    /// counts the purchase twice and the reported revenue doubles.
    /// </summary>
    public static string PurchaseEventId(string? orderNumber) =>
        "purchase-" + (orderNumber ?? "").Trim();

    public static bool LooksLikePixelId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Trim().All(char.IsDigit) && id.Trim().Length is >= 8 and <= 20;

    /// <summary>SHA-256, lower-case hex — Meta's required digest. Null for anything blank.</summary>
    public static string? Hash(string? normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string? NormalizeEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

    public static string? NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var s = new string(name.Where(c => !char.IsDigit(c) && !char.IsPunctuation(c)).ToArray());
        s = s.Trim().ToLowerInvariant();
        return s.Length == 0 ? null : s;
    }

    /// <summary>City/state/postcode: lower-case, letters and digits only.</summary>
    public static string? NormalizePlace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return s.Length == 0 ? null : s;
    }

    /// <summary>Two-letter ISO country, lower-cased. "Nigeria" and "NG" both become "ng".</summary>
    public static string? NormalizeCountry(string? country)
    {
        if (string.IsNullOrWhiteSpace(country)) return null;
        var s = country.Trim().ToLowerInvariant();
        if (s is "nigeria" or "ng") return "ng";
        var letters = new string(s.Where(char.IsLetter).ToArray());
        return letters.Length == 2 ? letters : null;
    }

    /// <summary>
    /// Digits only, including country code, no "+" — Meta's phone rule. Nigerian numbers are typed
    /// locally as 0803…, which has to become 234803… or it will not match a number Meta holds in
    /// international form.
    /// </summary>
    public static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var d = new string(phone.Where(char.IsDigit).ToArray());
        if (d.Length == 0) return null;
        if (d.StartsWith("234")) return d;                       // already international
        if (d.StartsWith("0") && d.Length == 11) return "234" + d[1..]; // 0803… → 234803…
        if (d.Length == 10) return "234" + d;                    // 803… typed without the trunk 0
        return d;                                                // some other country: leave as dialled
    }

    /// <summary>First/last name for Meta, preferring what the customer typed on the order itself.</summary>
    public static (string? First, string? Last) SplitName(string? fullName, string? fallbackFirst, string? fallbackLast)
    {
        if (!string.IsNullOrWhiteSpace(fullName))
        {
            var parts = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) return (parts[0], string.Join(' ', parts[1..]));
            if (parts.Length == 1) return (parts[0], fallbackLast);
        }
        return (fallbackFirst, fallbackLast);
    }
}
