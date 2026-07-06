namespace SterlingLams.Web.Services.Payment;

/// <summary>
/// The <see cref="IPaymentService"/> the app injects. It picks the active provider from settings
/// (Admin → Integrations, falling back to config) on every call, so switching gateway or updating
/// keys takes effect immediately with no redeploy. Each call resolves a fresh provider instance,
/// which reads its keys at that moment.
/// </summary>
public class PaymentRouter : IPaymentService
{
    private readonly IServiceProvider _sp;
    private readonly PaymentCredentials _creds;
    private string _lastProviderName = "Paystack";

    public PaymentRouter(IServiceProvider sp, PaymentCredentials creds)
    {
        _sp = sp;
        _creds = creds;
    }

    /// <summary>The provider used for the most recent operation (used to stamp Order.PaymentProvider).</summary>
    public string ProviderName => _lastProviderName;

    private async Task<IPaymentService> ResolveAsync()
    {
        var provider = await _creds.ProviderAsync();
        IPaymentService svc = provider switch
        {
            "stripe"      => _sp.GetRequiredService<StripePaymentService>(),
            "flutterwave" => _sp.GetRequiredService<FlutterwavePaymentService>(),
            _             => _sp.GetRequiredService<PaystackPaymentService>(),
        };
        _lastProviderName = svc.ProviderName;
        return svc;
    }

    public async Task<InitiatePaymentResult> InitiatePaymentAsync(InitiatePaymentRequest request)
        => await (await ResolveAsync()).InitiatePaymentAsync(request);

    public async Task<VerifyPaymentResult> VerifyPaymentAsync(string reference)
        => await (await ResolveAsync()).VerifyPaymentAsync(reference);

    public async Task<bool> ValidateWebhookAsync(string payload, string signature)
        => await (await ResolveAsync()).ValidateWebhookAsync(payload, signature);

    public async Task<RefundResult> RefundPaymentAsync(RefundPaymentRequest request)
        => await (await ResolveAsync()).RefundPaymentAsync(request);
}
