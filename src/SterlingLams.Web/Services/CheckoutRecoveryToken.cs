using Microsoft.AspNetCore.DataProtection;

namespace SterlingLams.Web.Services;

/// <summary>Signs/validates the token embedded in a Retainful abandoned-cart "recovery_url". The token
/// carries the order number, is tamper-proof (data-protected) and expires, so the recovery link can
/// rebuild the shopper's checkout without exposing or trusting a raw order id in the URL.</summary>
public static class CheckoutRecoveryToken
{
    private const string Purpose = "SterlingLams.CheckoutRecovery.v1";
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    public static string Create(IDataProtectionProvider dp, string orderNumber)
        => dp.CreateProtector(Purpose).ToTimeLimitedDataProtector().Protect(orderNumber, Lifetime);

    /// <summary>Returns the order number, or null when the token is missing, tampered or expired.</summary>
    public static string? Read(IDataProtectionProvider dp, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try { return dp.CreateProtector(Purpose).ToTimeLimitedDataProtector().Unprotect(token); }
        catch { return null; }
    }
}
