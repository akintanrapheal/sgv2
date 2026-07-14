namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Masks personally-identifiable values before they go into application logs, so log files /
/// aggregators don't store customer PII in clear text. Keep the domain (handy for debugging a
/// delivery issue) but hide the local part.
/// </summary>
public static class LogRedact
{
    /// <summary>"raphael@sterlinglams.com" → "r***@sterlinglams.com"; null/blank → "(none)".</summary>
    public static string Email(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "(none)";
        var e = email.Trim();
        var at = e.IndexOf('@');
        if (at <= 0) return e[0] + "***";        // no local part / malformed
        return e[0] + "***" + e[at..];           // first char + *** + @domain
    }
}
