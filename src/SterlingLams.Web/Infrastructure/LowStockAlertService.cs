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
        // Per-(product, branch) on-hand (summed over variant rows), keeping anything that's negative
        // (oversold), out of stock, or at/below its low-stock threshold.
        var raw = await db.StoreInventories
            .Where(si => si.Product.IsActive)
            .GroupBy(si => new { si.StoreId, StoreName = si.Store.Name, si.ProductId, si.Product.Name, si.Product.Sku, si.Product.LowStockThreshold })
            .Select(g => new
            {
                g.Key.StoreName, g.Key.Name, g.Key.Sku, g.Key.LowStockThreshold,
                OnHand = g.Sum(x => x.QuantityOnHand)
            })
            .Where(x => x.OnHand <= x.LowStockThreshold || x.OnHand <= 1)
            .ToListAsync(ct);

        // Classify (threshold floored at 1) and keep only real problems.
        var items = raw.Select(x =>
        {
            var thr = x.LowStockThreshold < 1 ? 1 : x.LowStockThreshold;
            var status = x.OnHand < 0 ? "Negative" : x.OnHand == 0 ? "Out" : x.OnHand <= thr ? "Low" : null;
            return new { Branch = x.StoreName.Replace("Sterlin Glams ", ""), x.Name, x.Sku, x.OnHand, Threshold = thr, Status = status };
        }).Where(x => x.Status != null).ToList();

        if (items.Count == 0) return; // nothing to flag → no email

        var negative = items.Count(x => x.Status == "Negative");
        var outOf = items.Count(x => x.Status == "Out");
        var lowCnt = items.Count(x => x.Status == "Low");
        var branchCount = items.Select(x => x.Branch).Distinct().Count();

        string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        string Badge(string? st) => st switch
        {
            "Negative" => "<span style=\"color:#b91c1c;font-weight:600;\">Negative</span>",
            "Out"      => "<span style=\"color:#dc2626;\">Out of stock</span>",
            _           => "<span style=\"color:#b45309;\">Low</span>"
        };

        // Group by branch; within a branch order Negative → Out → Low, then by on-hand.
        var rank = new Dictionary<string, int> { ["Negative"] = 0, ["Out"] = 1, ["Low"] = 2 };
        var sections = string.Join("", items
            .GroupBy(x => x.Branch).OrderBy(g => g.Key)
            .Select(g =>
            {
                var rows = string.Join("", g.OrderBy(x => rank[x.Status!]).ThenBy(x => x.OnHand).Take(100).Select(x =>
                    $"<tr><td style=\"padding:6px 0;border-bottom:1px solid #f0efed;\">{Enc(x.Name)}</td>" +
                    $"<td style=\"padding:6px 0 6px 16px;border-bottom:1px solid #f0efed;color:#78716c;\">{Enc(x.Sku)}</td>" +
                    $"<td align=\"right\" style=\"padding:6px 0 6px 16px;border-bottom:1px solid #f0efed;\">{x.OnHand}</td>" +
                    $"<td align=\"right\" style=\"padding:6px 0 6px 16px;border-bottom:1px solid #f0efed;color:#78716c;\">{x.Threshold}</td>" +
                    $"<td align=\"right\" style=\"padding:6px 0 6px 16px;border-bottom:1px solid #f0efed;\">{Badge(x.Status)}</td></tr>"));
                return $@"<h3 style=""font-size:15px;margin:18px 0 6px;"">{Enc(g.Key)} <span style=""color:#78716c;font-weight:400;"">— {g.Count()} item(s)</span></h3>
                    <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""font-size:14px;"">
                        <tr><th align=""left"">Product</th><th align=""left"" style=""padding-left:16px;"">SKU</th>
                            <th align=""right"" style=""padding-left:16px;"">On hand</th><th align=""right"" style=""padding-left:16px;"">Threshold</th>
                            <th align=""right"" style=""padding-left:16px;"">Status</th></tr>{rows}</table>";
            }));

        var body = $@"
            <h2 style=""font-size:18px;margin:0 0 6px;"">Stock alert — {items.Count} item(s) across {branchCount} branch(es)</h2>
            <p style=""color:#57534e;"">{negative} negative (oversold) · {outOf} out of stock · {lowCnt} low. Review in the Inventory Reorder report.</p>
            {sections}";

        var email = scope.ServiceProvider.GetRequiredService<IEmailService>();
        var subject = negative > 0
            ? $"Stock alert — {negative} oversold + {outOf + lowCnt} low/out"
            : $"Stock alert — {outOf} out, {lowCnt} low";
        var sent = await email.SendAsync(adminEmail, subject, body, ct: ct);
        if (sent)
        {
            _lastSentDate = today;
            _logger.LogInformation("Stock digest sent to {Email} ({Count} item(s), {Branches} branch(es)).", adminEmail, items.Count, branchCount);
        }
        else
        {
            _logger.LogWarning("Stock digest NOT sent (email disabled/failed); {Count} item(s) flagged.", items.Count);
        }
    }
}
