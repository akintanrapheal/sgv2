namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Compares scanned codes to stored barcodes ignoring a leading zero, so a label printed "2345" still
/// scans the item stored as "02345" (and vice versa) — EposNow/Excel exports routinely add or drop the
/// leading zero. <see cref="Key"/> is the comparison key: trimmed, lower-cased, leading zeros stripped.
/// </summary>
public static class BarcodeMatch
{
    /// <summary>The leading-zero-insensitive comparison key for a code ("02345" and "2345" → "2345").
    /// An all-zero / empty code keeps its trimmed, lower-cased form so it never collapses to "".</summary>
    public static string Key(string? code)
    {
        var s = (code ?? "").Trim().ToLowerInvariant();
        var k = s.TrimStart('0');
        return k.Length == 0 ? s : k;
    }

    /// <summary>True when two codes refer to the same barcode ignoring a leading zero. False if either is
    /// blank, so a blank scan never matches a blank-barcoded product.</summary>
    public static bool Same(string? a, string? b)
    {
        var ka = Key(a);
        var kb = Key(b);
        return ka.Length > 0 && ka == kb;
    }
}
