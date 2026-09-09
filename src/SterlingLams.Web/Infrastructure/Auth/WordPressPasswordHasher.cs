using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Infrastructure.Auth;

/// <summary>
/// Password hasher that lets customers migrated from the old WooCommerce/WordPress site sign in with
/// their EXISTING password, then transparently upgrades them to the modern ASP.NET Identity format.
///
/// New passwords are always written with the built-in Identity hasher (PBKDF2). On verify:
///   • a WordPress "portable" hash (<c>$P$</c> / <c>$H$</c>, phpass) is checked with the phpass algorithm;
///   • a bcrypt hash (<c>$2a$</c>/<c>$2b$</c>/<c>$2y$</c>, used by newer WP + WooCommerce setups) is
///     checked with BCrypt;
/// and when either matches we return <see cref="PasswordVerificationResult.SuccessRehashNeeded"/> so
/// Identity re-hashes and stores the password natively — the WordPress hash is used exactly once, on the
/// user's first login. Anything else (a normal Identity hash) is delegated to the default hasher, so
/// native accounts and new sign-ups are completely unaffected.
///
/// Note: WordPress 6.8+ introduces a <c>$wp$</c>-prefixed bcrypt variant with a pre-hash step; it is not
/// handled here yet because it needs validating against a real sample from the source site.
/// </summary>
public sealed class WordPressPasswordHasher : IPasswordHasher<ApplicationUser>
{
    private readonly PasswordHasher<ApplicationUser> _identity = new();

    // Always issue modern Identity (PBKDF2) hashes when the app sets a password.
    public string HashPassword(ApplicationUser user, string password)
        => _identity.HashPassword(user, password);

    public PasswordVerificationResult VerifyHashedPassword(
        ApplicationUser user, string hashedPassword, string providedPassword)
    {
        if (string.IsNullOrEmpty(hashedPassword) || providedPassword is null)
            return PasswordVerificationResult.Failed;

        // WordPress phpass "portable" hash.
        if (hashedPassword.StartsWith("$P$", StringComparison.Ordinal)
            || hashedPassword.StartsWith("$H$", StringComparison.Ordinal))
        {
            return PhpassVerify(providedPassword, hashedPassword)
                ? PasswordVerificationResult.SuccessRehashNeeded
                : PasswordVerificationResult.Failed;
        }

        // bcrypt (newer WordPress / WooCommerce, and some plugins).
        if (hashedPassword.StartsWith("$2a$", StringComparison.Ordinal)
            || hashedPassword.StartsWith("$2b$", StringComparison.Ordinal)
            || hashedPassword.StartsWith("$2y$", StringComparison.Ordinal))
        {
            bool ok;
            try { ok = BCrypt.Net.BCrypt.Verify(providedPassword, hashedPassword); }
            catch { ok = false; } // malformed hash → treat as a failed attempt, never throw
            return ok ? PasswordVerificationResult.SuccessRehashNeeded : PasswordVerificationResult.Failed;
        }

        // Native ASP.NET Identity hash (or anything else) → default behaviour.
        return _identity.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }

    // ── phpass (WordPress "portable" hashes) ────────────────────────────────────
    private const string Itoa64 = "./0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    private static bool PhpassVerify(string password, string storedHash)
    {
        var computed = CryptPrivate(Encoding.UTF8.GetBytes(password), storedHash);
        // A valid phpass hash is 34 chars; a failure path returns "*". Constant-time compare on match.
        return computed.Length == 34 && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(storedHash));
    }

    private static string CryptPrivate(byte[] password, string setting)
    {
        // Layout: "$P$" + 1 count char + 8 salt chars + 22 hash chars.
        if (setting.Length < 12) return "*0";
        if (setting[0] != '$' || setting[2] != '$') return "*0";

        var countLog2 = Itoa64.IndexOf(setting[3]);
        if (countLog2 < 7 || countLog2 > 30) return "*0";
        var count = 1 << countLog2;

        var salt = setting.Substring(4, 8);
        if (salt.Length != 8) return "*0";
        var saltBytes = Encoding.ASCII.GetBytes(salt);

        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Concat(saltBytes, password));
        do { hash = md5.ComputeHash(Concat(hash, password)); } while (--count > 0);

        return setting.Substring(0, 12) + Encode64(hash, hash.Length);
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }

    // phpass' custom base64 (little-endian, itoa64 alphabet).
    private static string Encode64(byte[] input, int count)
    {
        var sb = new StringBuilder();
        var i = 0;
        do
        {
            int value = input[i++];
            sb.Append(Itoa64[value & 0x3f]);
            if (i < count) value |= input[i] << 8;
            sb.Append(Itoa64[(value >> 6) & 0x3f]);
            if (i++ >= count) break;
            if (i < count) value |= input[i] << 16;
            sb.Append(Itoa64[(value >> 12) & 0x3f]);
            if (i++ >= count) break;
            sb.Append(Itoa64[(value >> 18) & 0x3f]);
        } while (i < count);
        return sb.ToString();
    }
}
