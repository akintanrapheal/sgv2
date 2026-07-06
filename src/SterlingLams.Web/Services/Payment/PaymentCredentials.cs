using SterlingLams.Web.Services;

namespace SterlingLams.Web.Services.Payment;

/// <summary>
/// Resolves live payment-gateway credentials at request time. Values entered in Admin → Integrations
/// (stored in SiteSettings, secrets encrypted at rest) take precedence; anything left blank falls
/// back to appsettings/environment config, so an untouched install behaves exactly as before.
/// Secret values arrive already-decrypted because <see cref="ISettingsService"/> reveals them on read.
/// </summary>
public class PaymentCredentials
{
    private readonly ISettingsService _settings;
    private readonly IConfiguration _config;

    public PaymentCredentials(ISettingsService settings, IConfiguration config)
    {
        _settings = settings;
        _config = config;
    }

    private static string First(params string?[] candidates)
        => candidates.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;

    /// <summary>The active provider (paystack | stripe | flutterwave).</summary>
    public async Task<string> ProviderAsync()
        => First(await _settings.GetAsync("payment.provider"), _config["Payment:Provider"], "Paystack")
            .ToLowerInvariant();

    public async Task<PaystackSettings> PaystackAsync() => new()
    {
        SecretKey = First(await _settings.GetAsync("payment.paystack.secret_key"), _config["Payment:Paystack:SecretKey"]),
        PublicKey = First(await _settings.GetAsync("payment.paystack.public_key"), _config["Payment:Paystack:PublicKey"]),
        BaseUrl   = First(_config["Payment:Paystack:BaseUrl"], "https://api.paystack.co"),
    };

    public async Task<StripeSettings> StripeAsync() => new()
    {
        SecretKey      = First(await _settings.GetAsync("payment.stripe.secret_key"), _config["Payment:Stripe:SecretKey"]),
        PublishableKey = First(await _settings.GetAsync("payment.stripe.publishable_key"), _config["Payment:Stripe:PublishableKey"]),
        WebhookSecret  = First(await _settings.GetAsync("payment.stripe.webhook_secret"), _config["Payment:Stripe:WebhookSecret"]),
    };

    public async Task<FlutterwaveSettings> FlutterwaveAsync() => new()
    {
        SecretKey     = First(await _settings.GetAsync("payment.flutterwave.secret_key"), _config["Payment:Flutterwave:SecretKey"]),
        PublicKey     = First(await _settings.GetAsync("payment.flutterwave.public_key"), _config["Payment:Flutterwave:PublicKey"]),
        EncryptionKey = First(await _settings.GetAsync("payment.flutterwave.encryption_key"), _config["Payment:Flutterwave:EncryptionKey"]),
        BaseUrl       = First(_config["Payment:Flutterwave:BaseUrl"], "https://api.flutterwave.com/v3"),
    };
}
