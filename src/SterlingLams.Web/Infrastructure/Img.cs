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

    // ── Delivery provider switch (Cloudinary → ImageKit) ────────────────────────────────────────────
    // ImageKit's free tier charges NO per-transformation credit (only bandwidth/storage), so moving
    // delivery there removes Cloudinary's credit cliff. Configured once at startup and whenever the
    // Integrations settings are saved (see Program.cs + IntegrationsController). When ImageKit is OFF the
    // Cloudinary path below is byte-for-byte unchanged, so this is a safe, dormant addition until enabled.
    // ImageKit must have a URL-endpoint whose origin is the Cloudinary delivery base
    // (https://res.cloudinary.com/<cloud>/image/upload/), so the same asset path resolves through it.
    private static volatile bool _ikOn;
    private static volatile string _ikEndpoint = "";
    public static void ConfigureDelivery(bool imageKitOn, string? imageKitEndpoint)
    {
        var ep = (imageKitEndpoint ?? "").Trim().TrimEnd('/');
        _ikEndpoint = ep;
        _ikOn = imageKitOn && ep.Length > 0;
    }

    // Snap every requested size to one of a few standard "buckets". Cloudinary bills a credit per 1,000
    // distinct transformations, and each unique width/height makes a new one — so a storefront asking for
    // 72/80/96/112/120/150/192/200/224/400/480/600/700/1000/1080… px all over the place multiplies the
    // credit cost. Rounding every request UP to the nearest bucket collapses that long tail into a handful
    // of shared, cached derivatives (never smaller than asked, so images stay crisp). The two hottest sizes
    // (96, 160) are kept exact so the most common thumbnails don't upsize. Change buckets here only.
    private static readonly int[] Buckets = { 96, 160, 256, 400, 512, 800, 1280, 1920 };
    private static int Snap(int px)
    {
        if (px <= 0) return Buckets[0];
        foreach (var b in Buckets) if (px <= b) return b;
        return ((px + 319) / 320) * 320; // beyond the largest bucket: round up to the next 320
    }

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
        var basePart = CloudinaryAssetPath(url)!;  // path after /upload/, minus any baked-in transform

        var w = Snap(width);

        // ImageKit delivery (free transformations). Same asset path, resolved via ImageKit's origin.
        if (_ikOn)
        {
            string tr = height is int hk
                ? (fill ? $"w-{w},h-{Snap(hk)},c-maintain_ratio,fo-auto" // crop to fill w×h (square cards)
                        : $"w-{w},h-{Snap(hk)},c-at_max")                 // fit within w×h, no crop
                : $"w-{w}";                                               // width-only, keep aspect
            return $"{_ikEndpoint}/{basePart}?tr={tr},f-auto,q-auto";
        }

        var t = height is int h
            ? $"f_auto,q_auto,w_{w},h_{Snap(h)},c_{(fill ? "fill" : "fit")}"
            : $"f_auto,q_auto,w_{w},c_limit"; // width-only: keep aspect, never upscale
        return url[..at] + t + "/" + basePart;
    }

    /// <summary>The asset path of a Cloudinary image URL — everything after <c>/image/upload/</c> with any
    /// baked-in delivery transform removed (e.g. "sterlinglams/products/abc.jpg"). This is the exact path
    /// Img.Cld requests from ImageKit, so the migration tool uploads originals to the Media Library under
    /// the same path and the same URLs then resolve from ImageKit's own storage. Null for non-Cloudinary.</summary>
    public static string? CloudinaryAssetPath(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var i = url.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var at = i + Marker.Length;
        var end = url.IndexOf('/', at);
        var firstSeg = end < 0 ? url[at..] : url[at..end];
        var isTransform = end > 0 && (firstSeg.Contains(',') || TransformSeg().IsMatch(firstSeg));
        var basePart = isTransform ? url[(end + 1)..] : url[at..];

        // Drop a Cloudinary version segment ("v1781727801/") so the path is clean + stable:
        // "sterlinglams/products/abc.jpg" instead of a per-version folder. Cloudinary serves the latest
        // version for a version-less public id, and ImageKit then stores ONE tidy path per asset (no
        // thousands of v… folders). Upload + delivery both use this, so they always match.
        if (basePart.Length > 2 && basePart[0] == 'v')
        {
            var k = 1;
            while (k < basePart.Length && char.IsDigit(basePart[k])) k++;
            if (k > 1 && k < basePart.Length && basePart[k] == '/') basePart = basePart[(k + 1)..];
        }
        return basePart;
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z]{1,3}_[^/]")]
    private static partial System.Text.RegularExpressions.Regex TransformSeg();
}
