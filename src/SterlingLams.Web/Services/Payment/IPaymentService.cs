namespace SterlingLams.Web.Services.Payment;

public interface IPaymentService
{
    string ProviderName { get; }
    Task<InitiatePaymentResult> InitiatePaymentAsync(InitiatePaymentRequest request);
    Task<VerifyPaymentResult> VerifyPaymentAsync(string reference);
    Task<bool> ValidateWebhookAsync(string payload, string signature);
}

public class InitiatePaymentRequest
{
    public string OrderNumber { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "NGN";
    public string CustomerEmail { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string CallbackUrl { get; set; } = string.Empty;
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public class InitiatePaymentResult
{
    public bool Success { get; set; }
    public string? AuthorizationUrl { get; set; }
    public string? Reference { get; set; }
    public string? ErrorMessage { get; set; }
}

public class VerifyPaymentResult
{
    public bool Success { get; set; }
    public bool IsPaid { get; set; }
    public string? Reference { get; set; }
    public string? OrderNumber { get; set; }
    public decimal AmountPaid { get; set; }
    public string? ErrorMessage { get; set; }
}
