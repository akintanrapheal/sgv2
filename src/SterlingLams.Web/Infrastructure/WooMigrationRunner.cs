using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services;
using SterlingLams.Web.Services.ERPNext;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// One-off / on-demand migration: replaces all website products with a WooCommerce CSV export
/// and reconciles ERPNext — disabling the previous Items and creating the imported ones.
/// Invoked from the command line: <c>dotnet run -- migrate-woo "C:\path\to\export.csv"</c>.
/// </summary>
public static class WooMigrationRunner
{
    public static async Task RunAsync(IServiceProvider services, string csvPath)
    {
        using var scope = services.CreateScope();
        var sp     = scope.ServiceProvider;
        var db     = sp.GetRequiredService<ApplicationDbContext>();
        var woo    = sp.GetRequiredService<IWooCommerceImportService>();
        var erp    = sp.GetRequiredService<IERPNextService>();
        var logger = sp.GetRequiredService<ILogger<ApplicationDbContext>>();

        void Line(string msg) { Console.WriteLine(msg); logger.LogInformation("[migrate-woo] {Msg}", msg); }

        if (!File.Exists(csvPath))
        {
            Console.Error.WriteLine($"CSV file not found: {csvPath}");
            return;
        }

        Line($"Starting WooCommerce migration from: {csvPath}");

        // ── 1. Capture the codes of the products we're about to remove ──────────
        var oldCodes = await db.Products
            .Where(p => p.ErpNextItemCode != "")
            .Select(p => p.ErpNextItemCode)
            .Distinct()
            .ToListAsync();
        Line($"Found {oldCodes.Count} existing product(s) with ERPNext codes to disable.");

        // ── 2. Disable the old Items in ERPNext (safe alternative to deletion) ──
        int disabled = 0, disableFailed = 0;
        foreach (var code in oldCodes)
        {
            try
            {
                if (await erp.SetItemDisabledAsync(code, true)) { disabled++; Line($"  disabled ERPNext item: {code}"); }
                else { disableFailed++; Line($"  could NOT disable: {code}"); }
            }
            catch (Exception ex) { disableFailed++; Line($"  error disabling {code}: {ex.Message}"); }
        }
        Line($"ERPNext disable complete: {disabled} disabled, {disableFailed} failed.");

        // ── 3. Run the WooCommerce CSV import (wipes website products, loads CSV) ─
        Line("Running WooCommerce CSV import (this clears existing products first)…");
        await using (var fs = File.OpenRead(csvPath))
        {
            var result = await woo.ImportFromCsvAsync(fs);
            Line($"Import result: {result.Summary}");
            foreach (var e in result.Errors.Take(10)) Line($"  import error: {e}");
        }

        // ── 4. Create the newly-imported products as ERPNext Items ──────────────
        var newItems = await db.Products
            .Where(p => p.ErpNextItemCode != "")
            .Select(p => new { p.ErpNextItemCode, p.Name, p.Price, p.Description })
            .ToListAsync();
        Line($"Creating {newItems.Count} item(s) in ERPNext…");

        int created = 0, existed = 0, createFailed = 0;
        foreach (var p in newItems)
        {
            try
            {
                var (ok, error) = await erp.CreateItemAsync(new ERPNextNewItemRequest
                {
                    ItemCode     = p.ErpNextItemCode,
                    ItemName     = p.Name,
                    StandardRate = p.Price,
                    Description  = p.Description
                });
                if (ok) created++;
                else if (error == null) existed++;
                else { createFailed++; Line($"  create failed {p.ErpNextItemCode}: {error}"); }
            }
            catch (Exception ex) { createFailed++; Line($"  error creating {p.ErpNextItemCode}: {ex.Message}"); }
        }

        Line($"ERPNext create complete: {created} created, {existed} already existed, {createFailed} failed.");
        Line("Migration finished.");
    }
}
