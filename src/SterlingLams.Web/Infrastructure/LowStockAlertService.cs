using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Sends the admin a digest of products at/below their low-stock threshold, when the
/// <c>notifications.low_stock</c> toggle is on. Mirrors <see cref="FulfilmentRetryService"/>: a
/// periodic sweep with a startup run. The send cadence is admin-set
/// (<c>notifications.low_stock_every_days</c>, default 1 = daily) and the last-sent date is
/// PERSISTED (<c>notifications.low_stock_last_sent</c>) so a restart/redeploy never re-sends.
/// </summary>
public class LowStockAlertService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private readonly IServiceProvider _sp;
    private readonly ILogger<LowStockAlertService> _logger;

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

        // Send at most once every N days (admin-set), using a PERSISTED last-sent date so a
        // restart/redeploy can't re-trigger it.
        var everyDays = Math.Max(1, await settings.GetIntAsync("notifications.low_stock_every_days", 1));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var lastSentStr = await settings.GetAsync("notifications.low_stock_last_sent", "");
        if (DateOnly.TryParse(lastSentStr, out var lastSent) && lastSent.AddDays(everyDays) > today)
            return; // not due yet

        var adminEmail = await settings.GetAsync("notifications.admin_email", "");
        // Per-store digest: each branch also gets a low-stock email for ITS OWN stock, to the store's
        // email. On by default; the admin all-branches digest still goes to notifications.admin_email.
        var perStore = await settings.GetBoolAsync("notifications.low_stock_per_store", true);
        if (string.IsNullOrWhiteSpace(adminEmail) && !perStore)
        {
            _logger.LogWarning("Low-stock alerts are on but no admin email and per-store digests are off.");
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

        static string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        static string Badge(string? st) => st switch
        {
            "Negative" => "<span style=\"color:#b91c1c;font-weight:600;\">Negative</span>",
            "Out"      => "<span style=\"color:#dc2626;\">Out of stock</span>",
            _           => "<span style=\"color:#b45309;\">Low</span>"
        };
        var rank = new Dictionary<string, int> { ["Negative"] = 0, ["Out"] = 1, ["Low"] = 2 };

        // Build a digest body from a set of flagged items (one section per branch).
        string BuildBody(IEnumerable<dynamic> list)
        {
            var ls = list.ToList();
            int neg = ls.Count(x => x.Status == "Negative"), o = ls.Count(x => x.Status == "Out"), lo = ls.Count(x => x.Status == "Low");
            var branches = ls.Select(x => (string)x.Branch).Distinct().Count();
            var sections = string.Join("", ls.GroupBy(x => (string)x.Branch).OrderBy(g => g.Key).Select(g =>
            {
                var rows = string.Join("", g.OrderBy(x => rank[(string)x.Status]).ThenBy(x => (int)x.OnHand).Take(200).Select(x =>
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
            return $@"<h2 style=""font-size:18px;margin:0 0 6px;"">Stock alert — {ls.Count} item(s) across {branches} branch(es)</h2>
                <p style=""color:#57534e;"">{neg} negative (oversold) · {o} out of stock · {lo} low. Review in the Inventory Reorder report.</p>{sections}";
        }
        string BuildSubject(IEnumerable<dynamic> list)
        {
            var ls = list.ToList();
            int neg = ls.Count(x => x.Status == "Negative"), o = ls.Count(x => x.Status == "Out"), lo = ls.Count(x => x.Status == "Low");
            return neg > 0 ? $"Stock alert — {neg} oversold + {o + lo} low/out" : $"Stock alert — {o} out, {lo} low";
        }

        var email = scope.ServiceProvider.GetRequiredService<IEmailService>();
        var anySent = false;

        // 1) Admin digest — every branch, to each configured admin email (comma-separated list).
        foreach (var addr in EmailRecipients.Split(adminEmail))
        {
            if (await email.SendAsync(addr, BuildSubject(items), BuildBody(items), ct: ct))
            {
                anySent = true;
                _logger.LogInformation("Low-stock admin digest sent to {Addr} ({Count} item(s)).", addr, items.Count);
            }
        }

        // 2) Per-store digest — each branch's own low stock, to that store's email.
        if (perStore)
        {
            var stores = await db.Stores.Where(s => s.IsActive && s.Email != null && s.Email != "")
                .Select(s => new { s.Name, s.Email }).ToListAsync(ct);
            foreach (var s in stores)
            {
                var branchKey = s.Name.Replace("Sterlin Glams ", "");
                var mine = items.Where(x => x.Branch == branchKey).Cast<dynamic>().ToList();
                if (mine.Count == 0) continue;
                if (await email.SendAsync(s.Email!, BuildSubject(mine), BuildBody(mine), ct: ct))
                {
                    anySent = true;
                    _logger.LogInformation("Low-stock digest sent to {Branch} ({Count} item(s)).", branchKey, mine.Count);
                }
            }
        }

        if (anySent)
            // Persist so restarts/redeploys don't re-send; honours the cadence next time.
            await settings.SaveManyAsync(new Dictionary<string, string> { ["notifications.low_stock_last_sent"] = today.ToString("yyyy-MM-dd") });
        else
            _logger.LogWarning("Stock digest NOT sent (no recipient/email disabled); {Count} item(s) flagged.", items.Count);
    }
}
