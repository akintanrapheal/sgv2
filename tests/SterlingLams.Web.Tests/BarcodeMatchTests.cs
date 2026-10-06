using SterlingLams.Web.Infrastructure;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>
/// POS scanning must match a label printed "2345" to the item stored as "02345" (and vice versa) — the
/// leading zero gets added/dropped by EposNow/Excel exports, which is why cashiers couldn't scan items in.
/// </summary>
public class BarcodeMatchTests
{
    [Theory]
    [InlineData("02345", "2345")]   // stored with zero, scanned without
    [InlineData("2345", "02345")]   // scanned with zero, stored without
    [InlineData("002345", "2345")]  // multiple leading zeros
    [InlineData("02345", "02345")]  // identical
    [InlineData(" 2345 ", "02345")] // whitespace from the scanner is ignored
    public void Same_treats_leading_zeros_as_equal(string a, string b)
        => Assert.True(BarcodeMatch.Same(a, b));

    [Theory]
    [InlineData("2345", "2346")]    // genuinely different codes
    [InlineData("12345", "2345")]   // a leading digit is not a leading zero
    [InlineData("", "02345")]       // blank never matches
    [InlineData(null, "0")]         // null never matches
    public void Same_rejects_different_or_blank_codes(string? a, string b)
        => Assert.False(BarcodeMatch.Same(a, b));
}
