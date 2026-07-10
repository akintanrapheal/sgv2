using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;

namespace SterlingLams.Web.Services.Payment;

public interface ISubscriptionPaymentService
{
    /// <summary>True once a developer Paystack secret key is configured (setting or env fallback).</summary>
    Task<bool> IsConfiguredAsync();

    Task<(bool ok, string? authorizationUrl, string? reference, string? error)> InitializeAsync(
        string email, decimal amountMajor, string currency, string callbackUrl, string reference, string description, string plan);

    Task<(bool ok, bool paid, string? error)> VerifyAsync(string reference);

    /// <summary>Validates a Paystack webhook body against the developer key (HMAC-SHA512).</summary>
    Task<bool> ValidateWebhookAsync(string payload, string signature);

    /// <summary>Activates the subscription for the given plan and returns the new renewal date (yyyy-MM-dd).
    /// Idempotent — safe to call from both the browser callback and the webhook.</summary>
    Task<string> ActivateAsync(string plan);

    /// <summary>Current USD→NGN rate: a manual override (subscription.usd_to_ngn) if set, else a cached
    /// live rate, else a safe fallback. Prices are shown in USD but charged in NGN at this rate.</summary>
    Task<decimal> GetUsdToNgnAsync();
}

/// <summary>
/// Paystack payments for the API-connector SUBSCRIPTION — credited to the developer's Paystack
/// account, which is separate from the storefront checkout (that credits the store). The developer
/// secret key comes from the encrypted setting <c>subscription.paystack_secret</c> (auto-decrypted by
/// SettingsService), or the <c>Subscription:PaystackSecret</c> config/env fallback.
/// </summary>
public class SubscriptionPaymentService : ISubscriptionPaymentService
{
    private readonly HttpClient _http;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ISettingsService _settings;
    private readonly IConfiguration _config;
    private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache _cache;
    private readonly ILogger<SubscriptionPaymentService> _logger;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private const decimal FallbackUsdToNgn = 1600m; // used only if a manual rate isn't set and the live fetch fails

    public SubscriptionPaymentService(HttpClient http, IHttpClientFactory httpFactory, ISettingsService settings,
        IConfiguration config, Microsoft.Extensions.Caching.Memory.IMemoryCache cache, ILogger<SubscriptionPaymentService> logger)
    {
        _http = http;
        _http.BaseAddress = new Uri(config["Subscription:PaystackBaseUrl"] ?? "https://api.paystack.co");
        _httpFactory = httpFactory;
        _settings = settings;
        _config = config;
        _cache = cache;
        _logger = logger;
    }

    public async Task<decimal> GetUsdToNgnAsync()
    {
        // 1) Manual override wins (lets the admin pin a parallel-market rate).
        if (decimal.TryParse(await _settings.GetAsync("subscription.usd_to_ngn", ""), out var manual) && manual > 0)
            return manual;

        // 2) Cached live rate (6h).
        if (_cache.TryGetValue("sub_usd_ngn", out decimal cached) && cached > 0)
            return cached;

        // 3) Fetch live from a free FX endpoint.
        try
        {
            var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            var json = await client.GetStringAsync("https://open.er-api.com/v6/latest/USD");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("rates", out var rates) && rates.TryGetProperty("NGN", out var ngn))
            {
                var rate = ngn.GetDecimal();
                if (rate > 0)
                {
                    _cache.Set("sub_usd_ngn", rate, TimeSpan.FromHours(6));
                    return rate;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live USD→NGN rate fetch failed; using fallback {Rate}", FallbackUsdToNgn);
        }
        return FallbackUsdToNgn;
    }

    private async Task<string> SecretAsync()
    {
        var s = await _settings.GetAsync("subscription.paystack_secret");
        if (string.IsNullOrWhiteSpace(s)) s = _config["Subscription:PaystackSecret"] ?? "";
        return s.Trim();
    }

    public async Task<bool> IsConfiguredAsync() => !string.IsNullOrWhiteSpace(await SecretAsync());

    private async Task ApplyAuthAsync()
        => _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await SecretAsync());

    public async Task<(bool ok, string? authorizationUrl, string? reference, string? error)> InitializeAsync(
        string email, decimal amountMajor, string currency, string callbackUrl, string reference, string description, string plan)
    {
        try
        {
            if (!await IsConfiguredAsync())
                return (false, null, null, "Subscription payments aren't configured yet — add your developer Paystack secret key on the Subscribe page.");

            await ApplyAuthAsync();
            var payload = new
            {
                email,
                amount = (int)Math.Round(amountMajor * 100), // Paystack expects the minor unit (kobo/cents)
                currency = string.IsNullOrWhiteSpace(currency) ? "NGN" : currency,
                reference,
                callback_url = callbackUrl,
                metadata = new { purpose = "api_connector_subscription", plan, description }
            };
            var resp = await _http.PostAsJsonAsync("/transaction/initialize", payload);
            var content = await resp.Content.ReadAsStringAsync();
            var r = JsonSerializer.Deserialize<InitResp>(content, Json);
            if (r?.Status == true && r.Data != null && !string.IsNullOrEmpty(r.Data.AuthorizationUrl))
                return (true, r.Data.AuthorizationUrl, r.Data.Reference, null);
            return (false, null, null, r?.Message ?? "Paystack did not accept the payment.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Subscription Paystack initialize failed");
            return (false, null, null, ex.Message);
        }
    }

    public async Task<(bool ok, bool paid, string? error)> VerifyAsync(string reference)
    {
        try
        {
            await ApplyAuthAsync();
            var resp = await _http.GetAsync($"/transaction/verify/{Uri.EscapeDataString(reference)}");
            var content = await resp.Content.ReadAsStringAsync();
            var r = JsonSerializer.Deserialize<VerifyResp>(content, Json);
            if (r?.Status == true && r.Data?.Status == "success")
                return (true, true, null);
            return (true, false, r?.Data?.Status ?? r?.Message ?? "not successful");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Subscription Paystack verify failed for {Reference}", reference);
            return (false, false, ex.Message);
        }
    }

    public async Task<bool> ValidateWebhookAsync(string payload, string signature)
    {
        var key = await SecretAsync();
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(signature)) return false;
        var hash = HMACSHA512.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(payload));
        var computed = Convert.ToHexString(hash).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed), Encoding.UTF8.GetBytes(signature.ToLowerInvariant()));
    }

    public async Task<string> ActivateAsync(string plan)
    {
        plan = plan == "yearly" ? "yearly" : "monthly";
        var renews = (plan == "yearly" ? DateTime.UtcNow.AddYears(1) : DateTime.UtcNow.AddMonths(1)).ToString("yyyy-MM-dd");
        await _settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["subscription.active"] = "true",
            ["subscription.plan"] = plan,
            ["subscription.renews_on"] = renews,
            ["general.trial_notice_enabled"] = "false", // clear the staff-wide reminder until renewal
        });
        return renews;
    }

    private class InitResp { public bool Status { get; set; } public string? Message { get; set; } public InitData? Data { get; set; } }
    private class InitData { [JsonPropertyName("authorization_url")] public string AuthorizationUrl { get; set; } = ""; public string Reference { get; set; } = ""; }
    private class VerifyResp { public bool Status { get; set; } public string? Message { get; set; } public VerifyData? Data { get; set; } }
    private class VerifyData { public string Status { get; set; } = ""; }
}
