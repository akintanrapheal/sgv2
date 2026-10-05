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

        var auth = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes(key + ":")));

        // Retry transient failures (429 rate-limit, 5xx, network) with exponential backoff — a burst of
        // 429s is what made the bulk Phase-2 copy fail en masse. Honour ImageKit's Retry-After when given.
        const int MaxAttempts = 4;
        string lastDetail = "unknown error";
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
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
                req.Headers.Authorization = auth;
                using var resp = await _http.SendAsync(req);
                if (resp.IsSuccessStatusCode) return (true, "ok");

                var body = await resp.Content.ReadAsStringAsync();
                var status = (int)resp.StatusCode;
                lastDetail = $"{status}: {(body.Length > 120 ? body[..120] : body)}";

                // 4xx other than 429 (bad path/auth/missing original) won't improve on retry — give up now.
                var transient = status == 429 || status >= 500;
                if (!transient || attempt == MaxAttempts)
                {
                    _log.LogWarning("ImageKit upload {Status} for {Path}: {Body}", status, assetPath,
                        body.Length > 200 ? body[..200] : body);
                    return (false, lastDetail);
                }
                await Task.Delay(BackoffMs(resp, attempt));
            }
            catch (Exception ex)
            {
                lastDetail = ex.Message;
                if (attempt == MaxAttempts) return (false, lastDetail);
                await Task.Delay(BackoffMs(null, attempt));
            }
        }
        return (false, lastDetail);
    }

    // Exponential backoff (0.5s, 1s, 2s), capped at 5s; prefers the server's Retry-After header on a 429.
    private static int BackoffMs(HttpResponseMessage? resp, int attempt)
    {
        var retryAfter = resp?.Headers.RetryAfter;
        double ms = 0;
        if (retryAfter?.Delta is TimeSpan d) ms = d.TotalMilliseconds;
        else if (retryAfter?.Date is DateTimeOffset until) ms = Math.Max(0, (until - DateTimeOffset.UtcNow).TotalMilliseconds);
        if (ms <= 0) ms = 500 * Math.Pow(2, attempt - 1);
        return (int)Math.Min(5000, ms);
    }
}
