using System.Linq;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Normalises Nigerian phone numbers so the same number typed in any common form is treated as one:
/// <c>+2349160009893</c>, <c>2349160009893</c>, <c>09160009893</c> and <c>9160009893</c> all share the
/// same <see cref="Key"/> ("9160009893"). Use <see cref="Key"/> for identity/matching and
/// <see cref="Canonical"/> for storage/display.
/// </summary>
public static class PhoneNumbers
{
    /// <summary>The 10-digit national significant number (drops a +234 / 234 country code or a single
    /// leading 0 trunk prefix), used as the identity key so the formats above all match. Returns the bare
    /// digits unchanged when it doesn't look like a Nigerian number, and "" when there are no digits.</summary>
    public static string Key(string? raw)
    {
        var d = new string((raw ?? "").Where(char.IsDigit).ToArray());
        if (d.Length == 0) return "";
        if (d.Length == 13 && d.StartsWith("234")) return d[3..];   // 2349160009893 -> 9160009893
        if (d.Length == 11 && d.StartsWith("0"))   return d[1..];   // 09160009893   -> 9160009893
        return d;                                                    // already 10-digit, or a foreign number
    }

    /// <summary>Canonical storage/display form: the local "0XXXXXXXXXX" (11-digit) for a Nigerian mobile —
    /// the everyday format and what the EposNow import + existing records use, so the same number always
    /// stores identically. Foreign/unrecognised numbers are left trimmed, as the customer typed them.
    /// (WhatsApp/Meta convert to +234 at send time via their own normalisers.)</summary>
    public static string? Canonical(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        var key = Key(raw);
        return key.Length == 10 ? "0" + key : raw.Trim();
    }
}
