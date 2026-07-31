using System.Text;

namespace SterlingLams.Web.Services;

/// <summary>
/// Writes RFC-4180 CSV fields. Two things every export here needs and hand-rolled string
/// interpolation kept getting wrong:
///
/// 1. <b>Escaping.</b> A value containing a quote, comma or newline must be quoted with its quotes
///    doubled. Wrapping a raw value in quote marks means one apostrophe-heavy product name or a
///    customer note with a line break shifts every column after it.
///
/// 2. <b>Formula injection.</b> Excel and Sheets execute a cell that starts with = + - @ (or a tab /
///    carriage return), so a customer who sets their name to <c>=HYPERLINK(...)</c> gets code running
///    on the machine of whoever opens the export. Prefixing an apostrophe makes it inert text.
/// </summary>
public static class Csv
{
    private static readonly char[] MustQuote = { ',', '"', '\n', '\r' };

    /// <summary>One CSV field: neutralised against formula injection, then quoted/escaped if needed.</summary>
    public static string Field(string? value)
    {
        var s = value ?? "";
        if (s.Length > 0 && (s[0] is '=' or '+' or '-' or '@' or '\t' or '\r'))
            s = "'" + s;
        return s.IndexOfAny(MustQuote) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    /// <summary>A whole CSV row, fields escaped, with a trailing newline.</summary>
    public static string Row(params string?[] fields) =>
        string.Join(",", fields.Select(Field)) + "\r\n";

    /// <summary>Appends an escaped row to <paramref name="sb"/>.</summary>
    public static void AppendRow(StringBuilder sb, params string?[] fields) => sb.Append(Row(fields));

    /// <summary>
    /// The finished file: a UTF-8 BOM so Excel reads ₦ and accented names correctly, then the body.
    /// </summary>
    public static byte[] ToBytes(StringBuilder sb) =>
        Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
}
