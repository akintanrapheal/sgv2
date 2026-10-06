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

    // ── Cloudflare Image Transformations (R2 era) ───────────────────────────────────────────────────
    // When ON, images are resized on the fly by Cloudflare via a /cdn-cgi/image/<opts>/<source> URL on the
    // R2 public domain (same zone, so no origin allow-list needed for R2 objects). Takes priority over
    // ImageKit/Cloudinary. Works for both the new R2 URLs and, during the transition, legacy Cloudinary
    // URLs (passed as an absolute source — which requires that host be allow-listed in the zone). OFF by
    // default, so this is a dormant addition until enabled in Admin → Integrations.
    private static volatile bool _cfOn;
    private static volatile string _cfHost = "";   // scheme+authority of the R2 public domain, e.g. https://img.sterlinglams.com
    private static volatile string _r2Base = "";   // full R2 public base, e.g. https://img.sterlinglams.com
    private static void SetR2Base(string? r2PublicBase)
    {
        var b = (r2PublicBase ?? "").Trim().TrimEnd('/');
        if (b.Length == 0) return;
        _r2Base = b;
        _cfHost = Uri.TryCreate(b, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Authority}" : "";
    }
    public static void ConfigureCloudflare(bool enabled, string? r2PublicBase)
    {
        SetR2Base(r2PublicBase);
        _cfOn = enabled && _cfHost.Length > 0;
    }

    // ── Pre-sized R2 renditions (the zero-cost path) ─────────────────────────────────────────────────
    // When ON, an R2 image is served as a fixed-width WebP file we generated at upload time
    // (…/<name>_800.webp) — a plain static download, so NO Cloudflare transformation is consumed (no
    // per-transform cost or quota). Takes priority over Cloudflare transforms. Only rewrites R2 URLs;
    // non-R2 URLs (e.g. Cloudinary Settings images) fall through to the transform/Cloudinary path. Turn on
    // ONLY after the renditions exist for every image (upload generates them; the batch backfills the rest),
    // or a missing …_<w>.webp would 404. OFF by default — dormant until enabled in Admin → Integrations.
    private static readonly int[] PreWidths = { 160, 400, 800, 1280 }; // must match ImageResizer.Widths
    private static volatile bool _presizedOn;
    public static void ConfigurePresized(bool enabled, string? r2PublicBase)
    {
        SetR2Base(r2PublicBase);
        _presizedOn = enabled && _r2Base.Length > 0;
    }

    // The pre-sized variant URL for an R2 image at a requested width, or null when it's not an R2 image
    // (so the caller falls through to the transform/Cloudinary path).
    private static string? Presized(string url, int width)
    {
        if (_r2Base.Length == 0 || !url.StartsWith(_r2Base, StringComparison.OrdinalIgnoreCase)) return null;
        var slash = url.LastIndexOf('/');
        var dot = url.LastIndexOf('.');
        if (dot <= slash) return null; // no file extension to swap
        var w = PreWidths[^1];
        foreach (var b in PreWidths) if (width <= b) { w = b; break; }
        return $"{url[..dot]}_{w}.webp";
    }

    // Builds a /cdn-cgi/image/ transform URL. R2 objects on the same zone use a relative source (no
    // allow-list); any other absolute URL is passed whole as the source.
    private static string CfTransform(string url, int w, int? height, bool fill)
    {
        var opts = height is int h
            ? $"width={w},height={Snap(h)},fit={(fill ? "cover" : "scale-down")},format=auto,quality=82"
            : $"width={w},fit=scale-down,format=auto,quality=82";

        if (_r2Base.Length > 0 && url.StartsWith(_r2Base, StringComparison.OrdinalIgnoreCase))
            return $"{_cfHost}/cdn-cgi/image/{opts}{url[_cfHost.Length..]}"; // relative source keeps its leading '/'
        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return $"{_cfHost}/cdn-cgi/image/{opts}/{url}";
        return url; // local/relative (dev) path — can't transform through Cloudflare
    }

    /// <summary>Scheme+host of the ACTIVE image CDN (ImageKit when enabled, else Cloudinary). Used for a
    /// &lt;link rel="preconnect"&gt; in the page head so the browser opens the TLS connection to the image
    /// host up front, instead of only after it parses the HTML and discovers the first &lt;img&gt; — which
    /// is what makes the first product images appear to "load slowly".</summary>
    public static string CdnOrigin
    {
        get
        {
            if ((_presizedOn || _cfOn) && _cfHost.Length > 0) return _cfHost;
            if (_ikOn && _ikEndpoint.Length > 0 &&
                Uri.TryCreate(_ikEndpoint, UriKind.Absolute, out var u))
                return $"{u.Scheme}://{u.Authority}";
            return "https://res.cloudinary.com";
        }
    }

    // Snap every requested size UP to one of a few standard "buckets". Both delivery providers bill per
    // UNIQUE transformation (Cloudflare Images: $0.50 per 1,000 beyond 5,000 free/month; Cloudinary: a
    // credit per 1,000), and every distinct width makes a new one — so a storefront asking for
    // 72/80/96/120/128/150/192/224/256/400/480/600/700/800/1000/1080/1200… px multiplies the cost.
    // Collapsing the long tail into FOUR buckets that match the real display sizes (thumbnails ~72–160,
    // cards ~400–600, detail ~800–1000, hero ~1080+) keeps the unique count low (≈ 4 sizes per image)
    // while staying crisp (never smaller than asked). Fewer buckets = fewer unique transformations = lower
    // bill; widen this list only if a layout genuinely needs another size. Change buckets here only.
    //   ≤160: all thumbnails · ≤400: cart/related/small cards · ≤800: product cards, category, home blocks
    //   ≤1280: product-detail main + most hero/journal · >1280: rounds to the next 320 (e.g. 1920 hero).
    private static readonly int[] Buckets = { 160, 400, 800, 1280 };
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

        // Pre-sized static R2 renditions first (zero transformation cost). Returns null for non-R2 URLs,
        // which then fall through to the Cloudflare/Cloudinary paths below.
        if (_presizedOn)
        {
            var pre = Presized(url, width);
            if (pre != null) return pre;
        }

        // Cloudflare transforms take priority and handle non-Cloudinary (R2) URLs too, so run this before
        // the Cloudinary-specific marker check below.
        if (_cfOn) return CfTransform(url, Snap(width), height, fill);

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
