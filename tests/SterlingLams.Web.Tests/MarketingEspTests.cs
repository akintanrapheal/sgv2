using SterlingLams.Web.Services.Marketing;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>The campaign sender only routes through an external ESP when <see cref="EspConfig.IsExternal"/>
/// is true; otherwise it falls back to SMTP. These lock that gate so a half-configured provider can never
/// silently drop a blast (no key, or no verified From → SMTP, not nothing).</summary>
public class MarketingEspTests
{
    [Fact]
    public void Smtp_provider_is_never_external()
        => Assert.False(new EspConfig("smtp", "re_x", "a@b.com", "SG", null, 100).IsExternal);

    [Theory]
    [InlineData("resend")]
    [InlineData("brevo")]
    public void External_requires_both_key_and_from(string provider)
    {
        Assert.False(new EspConfig(provider, "", "a@b.com", "SG", null, 100).IsExternal);   // no key
        Assert.False(new EspConfig(provider, "k", "", "SG", null, 100).IsExternal);          // no From
        Assert.True(new EspConfig(provider, "k", "a@b.com", "SG", null, 100).IsExternal);    // both → external
    }

    [Fact]
    public void Unknown_provider_is_never_external()
        => Assert.False(new EspConfig("mailchimp", "k", "a@b.com", "SG", null, 100).IsExternal);

    [Fact]
    public void Ses_requires_aws_keys_region_and_from()
    {
        // No AWS creds → not external (falls back to SMTP).
        Assert.False(new EspConfig("ses", "", "a@b.com", "SG", null, 100).IsExternal);
        // Missing secret / region each block it.
        Assert.False(new EspConfig("ses", "", "a@b.com", "SG", null, 100, AwsAccessKey: "AKIA", AwsSecretKey: "", AwsRegion: "us-east-1").IsExternal);
        Assert.False(new EspConfig("ses", "", "a@b.com", "SG", null, 100, AwsAccessKey: "AKIA", AwsSecretKey: "sk", AwsRegion: "").IsExternal);
        Assert.False(new EspConfig("ses", "", "", "SG", null, 100, AwsAccessKey: "AKIA", AwsSecretKey: "sk", AwsRegion: "us-east-1").IsExternal); // no From
        // Everything present → external.
        Assert.True(new EspConfig("ses", "", "a@b.com", "SG", null, 100, AwsAccessKey: "AKIA", AwsSecretKey: "sk", AwsRegion: "us-east-1").IsExternal);
    }

    [Fact]
    public void Ses_ignores_the_resend_brevo_api_key()
        => Assert.False(new EspConfig("ses", "re_x", "a@b.com", "SG", null, 100).IsExternal); // api_key alone doesn't configure SES
}
