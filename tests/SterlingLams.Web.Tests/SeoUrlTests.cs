using SterlingLams.Web.Infrastructure;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>
/// The canonical origin + path normalisation that collapses www/apex/mixed-case/trailing-slash copies of a
/// page onto one URL (the fix for GSC "Duplicate without user-selected canonical").
/// </summary>
public class SeoUrlTests
{
    [Theory]
    [InlineData("/Products/Shop/", "/products/shop")] // lower-cased + trailing slash stripped
    [InlineData("/products/abc", "/products/abc")]
    [InlineData("/", "/")]
    [InlineData("", "/")]
    [InlineData(null, "/")]
    public void NormalizePath_lowercases_and_strips_trailing_slash(string? input, string expected)
        => Assert.Equal(expected, SeoUrl.NormalizePath(input));

    [Fact]
    public void Canonical_uses_the_fixed_apex_base_regardless_of_request()
    {
        // Default base is the apex domain; a mixed-case path canonicalises to the lower-case apex URL.
        Assert.Equal("https://sterlinglams.com/product/gold-ring", SeoUrl.Canonical("/Product/Gold-Ring"));
    }

    [Fact]
    public void Configure_overrides_only_with_a_valid_absolute_url()
    {
        try
        {
            SeoUrl.Configure("  ");                         // blank → keeps apex default
            Assert.Equal("https://sterlinglams.com", SeoUrl.Base);
            SeoUrl.Configure("https://staging.example.com/"); // valid → overrides, trailing slash dropped
            Assert.Equal("https://staging.example.com", SeoUrl.Base);
        }
        finally
        {
            SeoUrl.Configure("https://sterlinglams.com");   // restore for any other test
        }
    }
}
