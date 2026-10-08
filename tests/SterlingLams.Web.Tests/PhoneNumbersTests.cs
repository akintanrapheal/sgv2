using SterlingLams.Web.Infrastructure;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>The same Nigerian number typed in any common form is treated as one — same identity Key and
/// same canonical storage. e.g. +2349160009893 and 09160009893 are the same number.</summary>
public class PhoneNumbersTests
{
    [Theory]
    [InlineData("+2349160009893", "9160009893")]
    [InlineData("2349160009893", "9160009893")]
    [InlineData("09160009893", "9160009893")]
    [InlineData("9160009893", "9160009893")]
    [InlineData("+234 916 000 9893", "9160009893")]
    public void Key_reduces_all_forms_to_the_same_national_number(string input, string expected)
        => Assert.Equal(expected, PhoneNumbers.Key(input));

    [Theory]
    [InlineData("+2349160009893", "09160009893")]
    [InlineData("09160009893", "09160009893")]
    [InlineData("9160009893", "09160009893")]
    [InlineData("+234 916 000 9893", "09160009893")]
    public void Canonical_stores_the_local_11_digit_form(string input, string expected)
        => Assert.Equal(expected, PhoneNumbers.Canonical(input));

    [Fact]
    public void The_two_example_formats_are_the_same_number()
    {
        Assert.Equal(PhoneNumbers.Key("+2349160009893"), PhoneNumbers.Key("09160009893"));
        Assert.Equal(PhoneNumbers.Canonical("+2349160009893"), PhoneNumbers.Canonical("09160009893"));
    }

    [Fact]
    public void A_foreign_or_odd_number_is_left_as_typed()
    {
        Assert.Equal("+15551234567", PhoneNumbers.Canonical("+15551234567")); // not NG (11 digits, no 0/234)
        Assert.Null(PhoneNumbers.Canonical(null));
    }
}
