namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Rewrites Cloudinary image URLs to serve right-sized, modern-format (WebP/AVIF) variants instead
/// of the full-resolution original. A 1.6 MB product PNG becomes ~70 KB at card size with no visible
/// quality loss. If a URL already carries a delivery-transform block (e.g. a variant image saved
/// with "w_200,h_200,c_fill" baked in), that block is REPLACED with the size we actually want — the
/// original full-res asset is unchanged on Cloudinary, so an undersized thumbnail becomes sharp again.
/// Non-Cloudinary URLs and blanks are returned unchanged, so it's always safe to wrap an <c>src</c>.
/// </summary>
public static partial class Img
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
        // Detect an existing delivery-transform block right after /upload/ (has commas, or starts with
        // a short "xx_" param like f_/q_/w_/h_/c_). A version segment ("v1712…") has no underscore, so
        // it's treated as the base, not a transform. When present, drop it and re-apply our own size.
        var isTransform = end > 0 && (firstSeg.Contains(',') || TransformSeg().IsMatch(firstSeg));
        var basePart = isTransform ? url[(end + 1)..] : url[at..];

        var t = height is int h
            ? $"f_auto,q_auto,w_{width},h_{h},c_{(fill ? "fill" : "fit")}"
            : $"f_auto,q_auto,w_{width},c_limit"; // width-only: keep aspect, never upscale
        return url[..at] + t + "/" + basePart;
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z]{1,3}_[^/]")]
    private static partial System.Text.RegularExpressions.Regex TransformSeg();
}
