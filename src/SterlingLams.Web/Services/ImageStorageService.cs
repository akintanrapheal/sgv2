using CloudinaryDotNet;
using CloudinaryDotNet.Actions;

namespace SterlingLams.Web.Services;

/// <summary>
/// One place that persists an uploaded image and returns its URL. Routes to the first available store:
/// <list type="number">
/// <item>Cloudflare R2 (when the "store in R2" switch is on + configured) — the target home for images.</item>
/// <item>Cloudinary (legacy) — used until the R2 cutover, and as a fallback if an R2 put fails.</item>
/// <item>Local wwwroot/uploads — dev only; NOT persistent on Render.</item>
/// </list>
/// Every upload call site (products, categories, avatars, social, imports) goes through this, so switching
/// storage is a single settings toggle with no code change at the call sites.
/// </summary>
public interface IImageStorageService
{
    /// <summary>Stores an uploaded file under <paramref name="subfolder"/> (e.g. "products"). Returns the
    /// public URL, or null on failure.</summary>
    Task<string?> UploadAsync(IFormFile file, string subfolder);

    /// <summary>Stores raw bytes (e.g. an image downloaded during product import). Returns the URL or null.</summary>
    Task<string?> UploadBytesAsync(byte[] bytes, string originalFileName, string subfolder);

    /// <summary>Generates + stores the pre-sized WebP renditions (…_160/400/800/1280.webp) for an R2 object
    /// key — used on upload and by the backfill batch. Best-effort; no-op when R2 isn't configured.</summary>
    Task GenerateVariantsAsync(string r2Key, byte[] originalBytes);
}

public sealed class ImageStorageService : IImageStorageService
{
    private readonly IR2Storage _r2;
    private readonly ICloudinaryProvider _cloud;
    private readonly IImageResizer _resizer;
    private readonly IWebHostEnvironment _env;

    public ImageStorageService(IR2Storage r2, ICloudinaryProvider cloud, IImageResizer resizer, IWebHostEnvironment env)
    {
        _r2 = r2;
        _cloud = cloud;
        _resizer = resizer;
        _env = env;
    }

    public async Task<string?> UploadAsync(IFormFile file, string subfolder)
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        return await StoreAsync(ms.ToArray(), Path.GetExtension(file.FileName), file.ContentType, subfolder);
    }

    public Task<string?> UploadBytesAsync(byte[] bytes, string originalFileName, string subfolder)
        => StoreAsync(bytes, Path.GetExtension(originalFileName), null, subfolder);

    private async Task<string?> StoreAsync(byte[] bytes, string ext, string? contentType, string subfolder)
    {
        ext = (ext ?? "").ToLowerInvariant();
        if (ext.Length == 0) ext = ".jpg";
        var sub = Sanitize(subfolder);
        var ct = string.IsNullOrWhiteSpace(contentType) ? R2Storage.ContentTypeFor(ext) : contentType!;

        // 1) Cloudflare R2 (the new home).
        if (await _r2.IsConfiguredAsync())
        {
            var key = $"sterlinglams/{(sub.Length > 0 ? sub + "/" : "")}{Guid.NewGuid():N}{ext}";
            var url = await _r2.PutAsync(key, bytes, ct);
            if (url != null)
            {
                await GenerateVariantsAsync(key, bytes); // pre-sized renditions for the zero-cost delivery path
                return url;
            }
            // fall through to Cloudinary if the put failed, so an upload is never silently lost
        }

        // 2) Cloudinary (legacy / fallback).
        var cloudinary = await _cloud.BuildAsync();
        if (cloudinary != null)
        {
            var folder = sub.Length == 0 ? "sterlinglams" : $"sterlinglams/{sub}";
            using var cs = new MemoryStream(bytes);
            var result = await cloudinary.UploadAsync(new ImageUploadParams
            {
                File = new FileDescription($"{Guid.NewGuid():N}{ext}", cs),
                Folder = folder,
                PublicId = Guid.NewGuid().ToString("N"),
                UniqueFilename = false,
                Overwrite = false,
            });
            if (result.StatusCode == System.Net.HttpStatusCode.OK && result.SecureUrl != null)
                return result.SecureUrl.ToString();
        }

        // 3) Local disk (dev only — wiped on Render redeploys).
        var folderPath = sub.Length == 0 ? "uploads" : $"uploads/{sub}";
        var dir = Path.Combine(_env.WebRootPath, folderPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);
        var fileName = $"{Guid.NewGuid():N}{ext}";
        await File.WriteAllBytesAsync(Path.Combine(dir, fileName), bytes);
        return $"/{folderPath}/{fileName}";
    }

    public async Task GenerateVariantsAsync(string r2Key, byte[] originalBytes)
    {
        if (string.IsNullOrWhiteSpace(r2Key) || originalBytes == null || originalBytes.Length == 0) return;
        if (!await _r2.IsConfiguredAsync()) return;
        var slash = r2Key.LastIndexOf('/');
        var dot = r2Key.LastIndexOf('.');
        var baseKey = dot > slash ? r2Key[..dot] : r2Key;
        var renditions = _resizer.ToWebpWidths(originalBytes);
        foreach (var (w, webp) in renditions)
            await _r2.PutAsync($"{baseKey}_{w}.webp", webp, "image/webp");
    }

    // Keep the subfolder to safe path segments (drops "." / ".." and empties), matching the old inline checks.
    private static string Sanitize(string? subfolder)
    {
        if (string.IsNullOrWhiteSpace(subfolder)) return "";
        var parts = subfolder.Trim('/', '\\').Split('/', '\\', StringSplitOptions.RemoveEmptyEntries)
            .Where(seg => seg is not ("." or ".."))
            .Select(seg => seg.Trim());
        return string.Join('/', parts);
    }
}
