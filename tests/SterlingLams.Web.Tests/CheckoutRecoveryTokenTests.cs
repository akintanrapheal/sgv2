using Microsoft.AspNetCore.DataProtection;
using SterlingLams.Web.Services;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>The abandoned-cart recovery link carries the order number in a data-protected token, so a
/// shopper can't rebuild someone else's checkout by guessing an order id.</summary>
public class CheckoutRecoveryTokenTests
{
    private static readonly IDataProtectionProvider Dp = new EphemeralDataProtectionProvider();

    [Fact]
    public void Round_trips_the_order_number()
    {
        var token = CheckoutRecoveryToken.Create(Dp, "SGW-30822");
        Assert.Equal("SGW-30822", CheckoutRecoveryToken.Read(Dp, token));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not-a-real-token")]
    public void Rejects_missing_or_tampered_tokens(string? bad)
        => Assert.Null(CheckoutRecoveryToken.Read(Dp, bad));

    [Fact]
    public void A_token_from_another_key_is_rejected()
    {
        var token = CheckoutRecoveryToken.Create(new EphemeralDataProtectionProvider(), "SGW-1");
        Assert.Null(CheckoutRecoveryToken.Read(Dp, token));   // different provider → can't unprotect
    }
}
