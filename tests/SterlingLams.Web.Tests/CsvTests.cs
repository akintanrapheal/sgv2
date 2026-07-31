using System.Text;
using SterlingLams.Web.Services;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>
/// The admin CSV exports used to interpolate raw values between quote marks, which breaks on any
/// value containing a quote (every following column shifts) and lets a value beginning with = + - @
/// execute as a formula when the file is opened in Excel or Sheets.
/// </summary>
public class CsvTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("has,comma", "\"has,comma\"")]
    [InlineData("has\"quote", "\"has\"\"quote\"")]
    [InlineData("has\nnewline", "\"has\nnewline\"")]
    public void Field_quotes_and_escapes_only_when_needed(string? input, string expected)
        => Assert.Equal(expected, Csv.Field(input));

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+44 800 000")]
    [InlineData("-5")]
    [InlineData("@handle")]
    public void Field_neutralises_values_a_spreadsheet_would_execute(string input)
    {
        var field = Csv.Field(input);
        // Prefixed with an apostrophe so the cell is inert text, never a formula.
        Assert.StartsWith("'", field.TrimStart('"'));
        Assert.Contains(input, field);
    }

    [Fact]
    public void Field_neutralises_the_classic_hyperlink_payload()
    {
        // A customer could set their own name to this; it must not survive as a live formula.
        var field = Csv.Field("=HYPERLINK(\"http://evil.test\",\"click\")");
        Assert.StartsWith("\"'=HYPERLINK(", field);
    }

    [Fact]
    public void Row_keeps_columns_aligned_when_a_value_contains_quotes_and_commas()
    {
        // The old string interpolation produced: "Bob "The Rock", Jr",bob@x.test  → columns shifted.
        // The email needs neither quoting nor an apostrophe: it contains no separator and doesn't
        // START with @, so a spreadsheet won't treat it as a formula.
        var row = Csv.Row("Bob \"The Rock\", Jr", "bob@x.test", "1500");
        Assert.Equal("\"Bob \"\"The Rock\"\", Jr\",bob@x.test,1500\r\n", row);
    }

    [Fact]
    public void ToBytes_starts_with_the_utf8_bom_so_excel_reads_naira_correctly()
    {
        var sb = new StringBuilder();
        Csv.AppendRow(sb, "Total");
        Csv.AppendRow(sb, "₦12,950.00");

        var bytes = Csv.ToBytes(sb);

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        // The naira amount contains a comma, so it must come back quoted, not split across columns.
        Assert.Contains("\"₦12,950.00\"", Encoding.UTF8.GetString(bytes));
    }
}
