namespace SterlingLams.Web.Infrastructure;

/// <summary>Tiny colour helpers for admin-chosen theme colours (e.g. the cart drawer background).</summary>
public static class ColorUtil
{
    /// <summary>True when a hex colour (#rgb / #rrggbb) is dark enough to need light text on top.
    /// Falls back to <paramref name="fallback"/> for blanks or anything unparseable.</summary>
    public static bool IsDark(string? hex, bool fallback = false)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var h = hex.Trim().TrimStart('#');
        if (h.Length == 3) h = string.Concat(h[0], h[0], h[1], h[1], h[2], h[2]);
        if (h.Length != 6
            || !int.TryParse(h.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            || !int.TryParse(h.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            || !int.TryParse(h.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            return fallback;
        // Perceived luminance (ITU-R BT.601). < 140 → treat as dark.
        return (0.299 * r + 0.587 * g + 0.114 * b) < 140;
    }
}
