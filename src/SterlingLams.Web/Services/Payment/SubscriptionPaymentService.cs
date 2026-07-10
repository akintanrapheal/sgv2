using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SterlingLams.Web.Services.Payment;

public interface ISubscriptionPaymentService
{
    /// <summary>True once a developer Paystack secret key is configured (setting or env fallback).</summary>
    Task<bool> IsConfiguredAsync();

    Task<(bool ok, string? authorizationUrl, string? reference, string? error)> InitializeAsync(
        string email, decimal amountMajor, string currency, string callbackUrl, string reference, string description);

    Task<(bool ok, bool paid, string? error)> VerifyAsync(string reference);
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
    private readonly ISettingsService _settings;
    private readonly IConfiguration _config;
    private readonly ILogger<SubscriptionPaymentService> _logger;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public SubscriptionPaymentService(HttpClient http, ISettingsService settings, IConfiguration config,
        ILogger<SubscriptionPaymentService> logger)
    {
        _http = http;
        _http.BaseAddress = new Uri(config["Subscription:PaystackBaseUrl"] ?? "https://api.paystack.co");
        _settings = settings;
        _config = config;
        _logger = logger;
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
        string email, decimal amountMajor, string currency, string callbackUrl, string reference, string description)
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
                metadata = new { purpose = "api_connector_subscription", description }
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

    private class InitResp { public bool Status { get; set; } public string? Message { get; set; } public InitData? Data { get; set; } }
    private class InitData { [JsonPropertyName("authorization_url")] public string AuthorizationUrl { get; set; } = ""; public string Reference { get; set; } = ""; }
    private class VerifyResp { public bool Status { get; set; } public string? Message { get; set; } public VerifyData? Data { get; set; } }
    private class VerifyData { public string Status { get; set; } = ""; }
}
