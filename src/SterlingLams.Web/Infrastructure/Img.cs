namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Rewrites Cloudinary image URLs to serve right-sized, modern-format (WebP/AVIF) variants instead
/// of the full-resolution original. A 1.6 MB product PNG becomes ~70 KB at card size with no visible
/// quality loss. Non-Cloudinary URLs, blanks, or already-transformed URLs are returned unchanged, so
/// it's always safe to wrap an <c>src</c>.
/// </summary>
public static class Img
{
    private const string Marker = "/image/upload/";

    /// <param name="url">The stored image URL.</param>
    /// <param name="width">Target display width in px (never upscales beyond the original).</param>
    /// <param name="height">Optional target height. When set, the image is cropped to fill w×h.</param>
    /// <param name="fill">true = crop to exact w×h (c_fill, for square cards); false = fit within (c_fit).</param>
    public static string? Cld(string? url, int width, int? height = null, bool fill = true)
    {
        if (string.IsNullOrEmpty(url)) return url;

        var i = url.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return url; // not a Cloudinary /image/upload/ URL — leave untouched

        var at = i + Marker.Length;
        var end = url.IndexOf('/', at);
        var firstSeg = end < 0 ? url[at..] : url[at..end];
        // Already carries a transform block (e.g. "f_auto,q_auto,w_600") — don't double-wrap.
        if (firstSeg.Contains(',') || firstSeg.StartsWith("f_") || firstSeg.StartsWith("w_")
            || firstSeg.StartsWith("q_") || firstSeg.StartsWith("c_"))
            return url;

        var t = height is int h
            ? $"f_auto,q_auto,w_{width},h_{h},c_{(fill ? "fill" : "fit")}"
            : $"f_auto,q_auto,w_{width},c_limit"; // width-only: keep aspect, never upscale
        return url[..at] + t + "/" + url[at..];
    }
}
