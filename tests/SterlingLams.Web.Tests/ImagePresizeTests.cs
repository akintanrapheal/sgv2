using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SterlingLams.Web.Infrastructure;
using SterlingLams.Web.Services;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>
/// The zero-cost image path: ImageResizer makes fixed-width WebP renditions, and Img serves the nearest
/// one straight from R2 (no Cloudflare transformation).
/// </summary>
public class ImagePresizeTests
{
    private static byte[] MakePng(int w, int h)
    {
        using var img = new Image<Rgba32>(w, h);
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Resizer_makes_a_webp_per_width_never_upscaling()
    {
        var resizer = new ImageResizer(NullLogger<ImageResizer>.Instance);
        var outp = resizer.ToWebpWidths(MakePng(1000, 500)); // original width 1000

        Assert.Equal(new[] { 160, 400, 800, 1280 }, outp.Keys.OrderBy(k => k).ToArray());
        foreach (var (bucket, bytes) in outp)
        {
            Assert.True(bytes.Length > 0);
            using var decoded = Image.Load(bytes);
            Assert.Equal(Math.Min(bucket, 1000), decoded.Width); // 1280 caps at the 1000px original
        }
    }

    [Fact]
    public void Resizer_returns_empty_on_non_image_bytes()
        => Assert.Empty(new ImageResizer(NullLogger<ImageResizer>.Instance).ToWebpWidths(new byte[] { 1, 2, 3, 4 }));

    [Fact]
    public void Resizer_rejects_tiff_uploads_so_the_tiff_decoder_is_never_reached()
    {
        // A perfectly valid TIFF, encoded via the full ImageSharp config...
        using var img = new Image<Rgba32>(64, 64);
        using var ms = new MemoryStream();
        img.SaveAsTiff(ms);

        // ...must be refused by the resizer, whose restricted config has no TIFF decoder registered.
        Assert.Empty(new ImageResizer(NullLogger<ImageResizer>.Instance).ToWebpWidths(ms.ToArray()));
    }

    [Fact]
    public void Cld_serves_the_nearest_presized_webp_for_r2_urls()
    {
        Img.ConfigurePresized(true, "https://img.sterlinglams.com");
        try
        {
            const string u = "https://img.sterlinglams.com/sterlinglams/products/abc.jpg";
            Assert.Equal("https://img.sterlinglams.com/sterlinglams/products/abc_800.webp", Img.Cld(u, 600));
            Assert.Equal("https://img.sterlinglams.com/sterlinglams/products/abc_160.webp", Img.Cld(u, 100));
            Assert.Equal("https://img.sterlinglams.com/sterlinglams/products/abc_1280.webp", Img.Cld(u, 5000)); // capped
            // A non-R2 URL isn't rewritten by the pre-sized path (falls through) — a local path stays as-is.
            Assert.Equal("/uploads/products/x.jpg", Img.Cld("/uploads/products/x.jpg", 400));
        }
        finally
        {
            Img.ConfigurePresized(false, "https://img.sterlinglams.com"); // don't leak state to other tests
        }
    }
}
