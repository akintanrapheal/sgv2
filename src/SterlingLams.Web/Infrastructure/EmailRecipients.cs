namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Parses a settings value that may hold SEVERAL email addresses (e.g. the admin-notification list) —
/// comma-, semicolon- or newline-separated — into distinct, trimmed, plausible addresses. Lets a single
/// setting fan a notification out to more than one recipient.
/// </summary>
public static class EmailRecipients
{
    private static readonly char[] Separators = { ',', ';', '\n', '\r' };

    public static IReadOnlyList<string> Split(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return System.Array.Empty<string>();
        return raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(a => a.Contains('@'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
