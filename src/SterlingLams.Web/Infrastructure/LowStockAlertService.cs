using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Sends the admin a once-a-day digest of products at/below their low-stock threshold, when the
/// <c>notifications.low_stock</c> toggle is on. Mirrors <see cref="FulfilmentRetryService"/>: a
/// periodic sweep with a startup run. Day-level dedupe keeps it to one email per day.
/// </summary>
public class LowStockAlertService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private readonly IServiceProvider _sp;
    private readonly ILogger<LowStockAlertService> _logger;
    private DateOnly? _lastSentDate; // in-memory: at most one digest per day (may re-send once after a restart)

    public LowStockAlertService(IServiceProvider sp, ILogger<LowStockAlertService> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Low-stock alert sweep failed."); }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        if (!await settings.GetBoolAsync("notifications.low_stock", false)) return;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (_lastSentDate == today) return; // already sent today

        var adminEmail = await settings.GetAsync("notifications.admin_email", "");
        if (string.IsNullOrWhiteSpace(adminEmail))
        {
            _logger.LogWarning("Low-stock alerts are on but notifications.admin_email is not set.");
            return;
        }

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        // Per-product total on-hand at/below the threshold (same rule as the Reorder report).
        var low = await db.Products
            .Where(p => p.IsActive)
            .Select(p => new
            {
                p.Name,
                p.Sku,
                p.LowStockThreshold,
                Total = p.StoreInventories.Sum(si => (int?)si.QuantityOnHand) ?? 0
            })
            .Where(x => x.Total <= (x.LowStockThreshold < 1 ? 1 : x.LowStockThreshold))
            .OrderBy(x => x.Total)
            .Take(100)
            .ToListAsync(ct);

        if (low.Count == 0) return; // nothing low → no email

        string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        var rows = string.Join("", low.Select(x =>
            $"<tr><td style=\"padding:6px 0;border-bottom:1px solid #f0efed;\">{Enc(x.Name)}</td>" +
            $"<td style=\"padding:6px 0 6px 16px;border-bottom:1px solid #f0efed;color:#78716c;\">{Enc(x.Sku)}</td>" +
            $"<td align=\"right\" style=\"padding:6px 0 6px 16px;border-bottom:1px solid #f0efed;\">{x.Total}</td>" +
            $"<td align=\"right\" style=\"padding:6px 0 6px 16px;border-bottom:1px solid #f0efed;color:#78716c;\">{x.LowStockThreshold}</td></tr>"));
        var body = $@"
            <h2 style=""font-size:18px;margin:0 0 12px;"">Low stock — {low.Count} product(s)</h2>
            <p>These products are at or below their low-stock threshold. Review them in the Inventory Reorder report.</p>
            <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""margin:16px 0;font-size:14px;"">
                <tr><th align=""left"">Product</th><th align=""left"" style=""padding-left:16px;"">SKU</th>
                    <th align=""right"" style=""padding-left:16px;"">On hand</th><th align=""right"" style=""padding-left:16px;"">Threshold</th></tr>
                {rows}
            </table>";

        var email = scope.ServiceProvider.GetRequiredService<IEmailService>();
        var sent = await email.SendAsync(adminEmail, $"Low stock alert — {low.Count} product(s)", body, ct: ct);
        if (sent)
        {
            _lastSentDate = today;
            _logger.LogInformation("Low-stock digest sent to {Email} ({Count} product(s)).", adminEmail, low.Count);
        }
        else
        {
            _logger.LogWarning("Low-stock digest NOT sent (email disabled/failed); {Count} product(s) low.", low.Count);
        }
    }
}
