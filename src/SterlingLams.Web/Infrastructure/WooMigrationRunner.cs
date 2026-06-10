using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// One-off / on-demand migration: replaces all website products with a WooCommerce CSV export.
/// Invoked from the command line: <c>dotnet run -- migrate-woo "C:\path\to\export.csv"</c>.
/// </summary>
public static class WooMigrationRunner
{
    public static async Task RunAsync(IServiceProvider services, string csvPath)
    {
        using var scope = services.CreateScope();
        var sp     = scope.ServiceProvider;
        var woo    = sp.GetRequiredService<IWooCommerceImportService>();
        var logger = sp.GetRequiredService<ILogger<ApplicationDbContext>>();

        void Line(string msg) { Console.WriteLine(msg); logger.LogInformation("[migrate-woo] {Msg}", msg); }

        if (!File.Exists(csvPath))
        {
            Console.Error.WriteLine($"CSV file not found: {csvPath}");
            return;
        }

        Line($"Starting WooCommerce migration from: {csvPath}");
        Line("Running WooCommerce CSV import (this clears existing products first)…");
        await using (var fs = File.OpenRead(csvPath))
        {
            var result = await woo.ImportFromCsvAsync(fs);
            Line($"Import result: {result.Summary}");
            foreach (var e in result.Errors.Take(10)) Line($"  import error: {e}");
        }
        Line("Migration finished.");
    }

    /// <summary>
    /// Decodes leftover HTML entities (e.g. &amp;#xD;&amp;#xA; newlines, &amp;#x2B; plus) in product
    /// descriptions that were imported before the importer decoded them. Website DB only — does not
    /// touch ERPNext. Usage: <c>dotnet run -- clean-product-text</c>.
    /// </summary>
    public static async Task CleanProductTextAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db     = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<ApplicationDbContext>>();

        static string? Decode(string? s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var d = System.Net.WebUtility.HtmlDecode(s).Replace("\r\n", "\n").Replace("\r", "\n");
            d = System.Text.RegularExpressions.Regex.Replace(d, @"[ \t]+", " ");
            d = System.Text.RegularExpressions.Regex.Replace(d, @"\n{3,}", "\n\n");
            return string.Join("\n", d.Split('\n').Select(l => l.TrimEnd())).Trim();
        }

        var products = await db.Products.ToListAsync();
        int changed = 0;
        foreach (var p in products)
        {
            var newDesc  = Decode(p.Description);
            var newShort = Decode(p.ShortDescription);
            if (newDesc != p.Description || newShort != p.ShortDescription)
            {
                p.Description = newDesc;
                p.ShortDescription = newShort;
                changed++;
            }
        }
        await db.SaveChangesAsync();
        Console.WriteLine($"Cleaned product text: {changed} of {products.Count} product(s) updated.");
        logger.LogInformation("[clean-product-text] {Changed}/{Total} products updated.", changed, products.Count);
    }
}
