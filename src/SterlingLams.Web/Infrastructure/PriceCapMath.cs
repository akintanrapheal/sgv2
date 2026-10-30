namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Pure math for the "Cap discounts at 15%" tool, kept out of the controller so it's unit-tested on its
/// own. A price is "capped" when it's discounted MORE than 15% off its regular price; the cap raises it to
/// exactly 15% off. A price at exactly 15% off (or less of a discount) is never touched.
/// </summary>
public static class PriceCapMath
{
    /// <summary>Fraction of the regular price the customer pays at the cap (15% off ⇒ 0.85).</summary>
    public const decimal CapFactor = 0.85m;

    /// <summary>The capped price: regular × 0.85, rounded UP to the whole naira so the resulting discount
    /// is at most 15% (never over). e.g. ₦10,000 → ₦8,500; ₦4,999 → ₦4,250 (14.98% off).</summary>
    public static decimal NewPrice(decimal regular) => System.Math.Ceiling(regular * CapFactor);

    /// <summary>True when <paramref name="value"/> is more than 15% below <paramref name="regular"/> — i.e.
    /// it should be raised. Exactly 15% off, a smaller discount, or a non-positive regular price → false.</summary>
    public static bool ShouldCap(decimal regular, decimal value) => regular > 0m && value < regular * CapFactor;
}
