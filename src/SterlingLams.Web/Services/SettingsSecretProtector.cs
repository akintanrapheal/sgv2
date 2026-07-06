using Microsoft.AspNetCore.DataProtection;

namespace SterlingLams.Web.Services;

/// <summary>
/// Encrypts/decrypts sensitive site-setting values (payment keys, SMTP password) at rest, so a
/// database dump never leaks live credentials. Encrypted values are stored with an "enc:" prefix
/// so reads are self-describing — anything without the prefix is treated as plaintext (legacy /
/// non-secret), which keeps the change backward-compatible.
/// </summary>
public interface ISettingsSecretProtector
{
    /// <summary>True if the stored value is one we encrypted (has the enc: sentinel).</summary>
    bool IsEncrypted(string? stored);

    /// <summary>Encrypts a plaintext secret for storage. Blank in → blank out (nothing to protect).</summary>
    string Protect(string? plaintext);

    /// <summary>Returns the plaintext for a stored value — decrypting if it's one of ours, else
    /// returning it unchanged. Never throws; a value that fails to decrypt comes back empty.</summary>
    string Reveal(string? stored);
}

public class SettingsSecretProtector : ISettingsSecretProtector
{
    private const string Prefix = "enc:";
    private readonly IDataProtector _protector;

    public SettingsSecretProtector(IDataProtectionProvider provider)
        => _protector = provider.CreateProtector("SterlingLams.SiteSettings.Secrets.v1");

    public bool IsEncrypted(string? stored)
        => !string.IsNullOrEmpty(stored) && stored.StartsWith(Prefix, StringComparison.Ordinal);

    public string Protect(string? plaintext)
        => string.IsNullOrEmpty(plaintext) ? string.Empty : Prefix + _protector.Protect(plaintext);

    public string Reveal(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal))
            return stored ?? string.Empty;
        try { return _protector.Unprotect(stored[Prefix.Length..]); }
        catch { return string.Empty; }
    }
}
