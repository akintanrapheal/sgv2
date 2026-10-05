using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace SterlingLams.Web.Services;

/// <summary>
/// Stores image originals in a Cloudflare R2 bucket (S3-compatible). R2 gives zero egress fees and a
/// bigger free tier than ImageKit's media library, and — because it lives on the same Cloudflare zone as
/// the site — Cloudflare Image Transformations resize its objects on the fly (see <c>Img</c>). Credentials
/// are settings-first (Admin → Integrations, secret encrypted) with no env fallback: R2 is owner-configured.
/// Returns null / (false, …) when R2 isn't configured, so callers fall back to Cloudinary or local disk.
/// </summary>
public interface IR2Storage
{
    /// <summary>True when endpoint + bucket + public base + access key + secret are all set AND the
    /// "store in R2" switch is on.</summary>
    Task<bool> IsConfiguredAsync();

    /// <summary>The public base URL for served objects (e.g. https://img.sterlinglams.com), or "".</summary>
    Task<string> PublicBaseAsync();

    /// <summary>Uploads bytes to <paramref name="key"/>. Returns the public URL, or null on failure.</summary>
    Task<string?> PutAsync(string key, byte[] content, string contentType);

    /// <summary>Downloads <paramref name="sourceUrl"/> and stores it at <paramref name="key"/> — used by the
    /// Cloudinary→R2 migration. Idempotent (overwrites). Returns (true,publicUrl) or (false,detail).</summary>
    Task<(bool ok, string detail)> PutFromUrlAsync(string key, string sourceUrl);
}

public sealed class R2Storage : IR2Storage
{
    private readonly ISettingsService _settings;
    private readonly ISettingsSecretProtector _secrets;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<R2Storage> _log;

    public R2Storage(ISettingsService settings, ISettingsSecretProtector secrets,
        IHttpClientFactory httpFactory, ILogger<R2Storage> log)
    {
        _settings = settings;
        _secrets = secrets;
        _httpFactory = httpFactory;
        _log = log;
    }

    private async Task<(string endpoint, string bucket, string publicBase, string accessKey, string secret)> ResolveAsync()
    {
        var endpoint   = (await _settings.GetAsync("r2.s3_endpoint", "")).Trim().TrimEnd('/');
        var bucket     = (await _settings.GetAsync("r2.bucket", "")).Trim();
        var publicBase = (await _settings.GetAsync("r2.public_base", "")).Trim().TrimEnd('/');
        var accessKey  = (await _settings.GetAsync("r2.access_key_id", "")).Trim();
        var secret     = _secrets.Reveal(await _settings.GetAsync("r2.secret_access_key", "")).Trim();
        return (endpoint, bucket, publicBase, accessKey, secret);
    }

    public async Task<string> PublicBaseAsync() => (await _settings.GetAsync("r2.public_base", "")).Trim().TrimEnd('/');

    public async Task<bool> IsConfiguredAsync()
    {
        if (!await _settings.GetBoolAsync("r2.enabled", false)) return false;
        var (ep, b, pub, ak, sk) = await ResolveAsync();
        return ep.Length > 0 && b.Length > 0 && pub.Length > 0 && ak.Length > 0 && sk.Length > 0;
    }

    // A client built per operation (uploads are infrequent). R2 needs region "auto" for SigV4 signing, and
    // the AWS SDK v4's default integrity checksums must be relaxed to WHEN_REQUIRED or R2 rejects the PUT.
    private async Task<(AmazonS3Client? client, string bucket, string publicBase)> BuildAsync()
    {
        var (ep, bucket, pub, ak, sk) = await ResolveAsync();
        if (ep.Length == 0 || bucket.Length == 0 || pub.Length == 0 || ak.Length == 0 || sk.Length == 0)
            return (null, bucket, pub);

        var config = new AmazonS3Config
        {
            ServiceURL = ep,
            ForcePathStyle = true,
            AuthenticationRegion = "auto",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        return (new AmazonS3Client(ak, sk, config), bucket, pub);
    }

    public async Task<string?> PutAsync(string key, byte[] content, string contentType)
    {
        key = key.TrimStart('/');
        var (client, bucket, pub) = await BuildAsync();
        if (client == null) return null;
        using (client)
        {
            try
            {
                using var ms = new MemoryStream(content);
                await client.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    InputStream = ms,
                    ContentType = contentType,
                    DisablePayloadSigning = true, // R2 doesn't support streaming SigV4 payload signing
                });
                return $"{pub}/{key}";
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "R2 PutObject failed for {Key}", LogSafe(key));
                return null;
            }
        }
    }

    public async Task<(bool ok, string detail)> PutFromUrlAsync(string key, string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(sourceUrl))
            return (false, "Missing key/source.");

        byte[] bytes;
        string contentType;
        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(60);
            using var resp = await http.GetAsync(sourceUrl);
            if (!resp.IsSuccessStatusCode) return (false, $"source {(int)resp.StatusCode}");
            bytes = await resp.Content.ReadAsByteArrayAsync();
            contentType = resp.Content.Headers.ContentType?.MediaType ?? ContentTypeFor(key);
        }
        catch (Exception ex) { return (false, $"download: {ex.Message}"); }

        var url = await PutAsync(key, bytes, contentType);
        return url != null ? (true, url) : (false, "R2 put failed");
    }

    // Strip CR/LF from a value before it goes into a log line, so a crafted object key can't forge extra
    // log entries (CWE-117 log injection).
    private static string LogSafe(string s) => (s ?? "").Replace("\r", "").Replace("\n", "");

    public static string ContentTypeFor(string pathOrExt)
    {
        var ext = Path.GetExtension(pathOrExt).ToLowerInvariant();
        if (ext.Length == 0) ext = pathOrExt.StartsWith('.') ? pathOrExt.ToLowerInvariant() : "";
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream",
        };
    }
}
