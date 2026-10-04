using System.Net.Http.Headers;
using System.Text;

namespace SterlingLams.Web.Services;

/// <summary>
/// Uploads originals into ImageKit's Media Library via the upload API, at a GIVEN asset path, so the same
/// delivery URLs (see <c>Img.CloudinaryAssetPath</c>) resolve from ImageKit's own storage instead of the
/// Cloudinary origin. Used by the Phase-2 "copy images into ImageKit" migration so Cloudinary can be
/// dropped. Needs the ImageKit private key (Admin → Integrations). Uploads by source URL (ImageKit fetches
/// the original itself), so the local machine doesn't stream the bytes.
/// </summary>
public interface IImageKitUploader
{
    Task<bool> IsConfiguredAsync();
    /// <param name="assetPath">Path the file must live at, e.g. "sterlinglams/products/abc.jpg".</param>
    /// <param name="sourceUrl">Where ImageKit fetches the original from (the current Cloudinary URL).</param>
    Task<(bool ok, string detail)> UploadByUrlAsync(string assetPath, string sourceUrl);
}

public class ImageKitUploader : IImageKitUploader
{
    private const string UploadUrl = "https://upload.imagekit.io/api/v1/files/upload";

    private readonly HttpClient _http;
    private readonly ISettingsService _settings;
    private readonly ISettingsSecretProtector _secrets;
    private readonly ILogger<ImageKitUploader> _log;

    public ImageKitUploader(HttpClient http, ISettingsService settings,
        ISettingsSecretProtector secrets, ILogger<ImageKitUploader> log)
    {
        _http = http;
        _settings = settings;
        _secrets = secrets;
        _log = log;
    }

    private async Task<string> PrivateKeyAsync() =>
        _secrets.Reveal(await _settings.GetAsync("imagekit.private_key", ""));

    public async Task<bool> IsConfiguredAsync()
    {
        var key = await PrivateKeyAsync();
        var ep = (await _settings.GetAsync("imagekit.url_endpoint", "")).Trim();
        return !string.IsNullOrWhiteSpace(key) && ep.Length > 0;
    }

    public async Task<(bool ok, string detail)> UploadByUrlAsync(string assetPath, string sourceUrl)
    {
        var key = await PrivateKeyAsync();
        if (string.IsNullOrWhiteSpace(key)) return (false, "ImageKit private key not set.");
        if (string.IsNullOrWhiteSpace(assetPath) || string.IsNullOrWhiteSpace(sourceUrl)) return (false, "Missing path/source.");

        var slash = assetPath.LastIndexOf('/');
        var fileName = slash >= 0 ? assetPath[(slash + 1)..] : assetPath;
        var folder = slash >= 0 ? "/" + assetPath[..slash] : "/";
        if (fileName.Length == 0) return (false, "Empty file name.");

        try
        {
            using var form = new MultipartFormDataContent
            {
                { new StringContent(sourceUrl), "file" },        // ImageKit fetches the original from this URL
                { new StringContent(fileName), "fileName" },
                { new StringContent(folder), "folder" },
                { new StringContent("false"), "useUniqueFileName" }, // keep the exact name so the path matches
                { new StringContent("true"), "overwriteFile" },      // idempotent: re-running updates in place
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, UploadUrl) { Content = form };
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes(key + ":")));
            using var resp = await _http.SendAsync(req);
            if (resp.IsSuccessStatusCode) return (true, "ok");
            var body = await resp.Content.ReadAsStringAsync();
            _log.LogWarning("ImageKit upload {Status} for {Path}: {Body}", (int)resp.StatusCode, assetPath,
                body.Length > 200 ? body[..200] : body);
            return (false, $"{(int)resp.StatusCode}: {(body.Length > 120 ? body[..120] : body)}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
