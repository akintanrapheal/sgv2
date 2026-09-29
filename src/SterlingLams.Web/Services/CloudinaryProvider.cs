using CloudinaryDotNet;

namespace SterlingLams.Web.Services;

/// <summary>
/// Single source of the Cloudinary client for every upload path. Resolves credentials settings-first
/// (Admin → Integrations, secrets decrypted) with an env/appsettings fallback (<c>Cloudinary:CloudName</c>
/// etc.), exactly like the payment/SMTP/analytics integrations. Returns null when Cloudinary isn't
/// configured, so callers fall back to storing the original URL / a local file.
/// </summary>
public interface ICloudinaryProvider
{
    /// <summary>A ready Cloudinary client, or null when credentials aren't configured.</summary>
    Task<Cloudinary?> BuildAsync();
    /// <summary>True when a cloud name + API key + secret are all resolvable.</summary>
    Task<bool> IsConfiguredAsync();
}

public sealed class CloudinaryProvider : ICloudinaryProvider
{
    private readonly ISettingsService _settings;
    private readonly ISettingsSecretProtector _secrets;
    private readonly IConfiguration _config;

    public CloudinaryProvider(ISettingsService settings, ISettingsSecretProtector secrets, IConfiguration config)
    {
        _settings = settings;
        _secrets = secrets;
        _config = config;
    }

    private async Task<(string cloudName, string apiKey, string apiSecret)> ResolveAsync()
    {
        var cloudName = (await _settings.GetAsync("cloudinary.cloud_name", "")).Trim();
        if (cloudName.Length == 0) cloudName = (_config["Cloudinary:CloudName"] ?? "").Trim();

        var apiKey = _secrets.Reveal(await _settings.GetAsync("cloudinary.api_key", "")).Trim();
        if (apiKey.Length == 0) apiKey = (_config["Cloudinary:ApiKey"] ?? "").Trim();

        var apiSecret = _secrets.Reveal(await _settings.GetAsync("cloudinary.api_secret", "")).Trim();
        if (apiSecret.Length == 0) apiSecret = (_config["Cloudinary:ApiSecret"] ?? "").Trim();

        return (cloudName, apiKey, apiSecret);
    }

    public async Task<bool> IsConfiguredAsync()
    {
        var (cn, ak, asec) = await ResolveAsync();
        return cn.Length > 0 && ak.Length > 0 && asec.Length > 0;
    }

    public async Task<Cloudinary?> BuildAsync()
    {
        var (cn, ak, asec) = await ResolveAsync();
        if (cn.Length == 0 || ak.Length == 0 || asec.Length == 0) return null;
        return new Cloudinary(new Account(cn, ak, asec)) { Api = { Secure = true } };
    }
}
