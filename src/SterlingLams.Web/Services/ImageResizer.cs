using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace SterlingLams.Web.Services;

/// <summary>
/// Pre-generates a few fixed-width WebP renditions of an uploaded image so the storefront can serve a
/// right-sized file DIRECTLY from R2 — no Cloudflare on-the-fly transformation (and therefore no
/// per-transform cost or quota). Width-only, aspect preserved, never upscaled beyond the original; the
/// square product cards crop via CSS (object-cover), so width-only matches today's look. Uses ImageSharp
/// 2.1.x (Apache-2.0), fully managed, no native dependencies on Render.
///
/// Security: decoding runs through a <see cref="Configuration"/> that registers ONLY the formats we accept
/// (JPEG/PNG/WebP/GIF). The TIFF decoder is therefore never reachable, which closes the ImageSharp TIFF /
/// BigTIFF advisories (GHSA TIFF T4/T6 encoder + BigTIFF decoder) — an uploaded .tiff is simply rejected
/// as an unknown format and returns empty here.
/// </summary>
public interface IImageResizer
{
    /// <summary>The widths rendered for every image (see <see cref="Img"/> which serves the nearest one).</summary>
    static readonly int[] Widths = { 160, 400, 800, 1280 };

    /// <summary>Decode <paramref name="source"/> once and return a WebP rendition per width in
    /// <see cref="Widths"/> (capped at the original width so nothing upscales). Empty on decode failure.</summary>
    IReadOnlyDictionary<int, byte[]> ToWebpWidths(byte[] source);
}

public sealed class ImageResizer : IImageResizer
{
    // A decode configuration that registers ONLY the formats we accept. Anything else (notably TIFF) is
    // rejected as an unknown format, so the vulnerable TIFF/BigTIFF code paths are never reached.
    private static readonly Configuration DecodeConfig = new(
        new JpegConfigurationModule(),
        new PngConfigurationModule(),
        new WebpConfigurationModule(),
        new GifConfigurationModule());

    private readonly ILogger<ImageResizer> _log;
    public ImageResizer(ILogger<ImageResizer> log) => _log = log;

    public IReadOnlyDictionary<int, byte[]> ToWebpWidths(byte[] source)
    {
        var result = new Dictionary<int, byte[]>();
        if (source == null || source.Length == 0) return result;
        try
        {
            using var image = Image.Load(DecodeConfig, source);
            var originalWidth = image.Width;
            var encoder = new WebpEncoder { Quality = 80 };
            foreach (var w in IImageResizer.Widths)
            {
                var target = Math.Min(w, originalWidth); // never upscale
                using var clone = image.Clone(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = new Size(target, 0),   // height 0 = keep aspect ratio
                    Mode = ResizeMode.Max,
                }));
                using var ms = new MemoryStream();
                clone.Save(ms, encoder);
                result[w] = ms.ToArray();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ImageResizer failed to process a {Bytes}-byte image", source.Length);
            return new Dictionary<int, byte[]>();
        }
        return result;
    }
}
