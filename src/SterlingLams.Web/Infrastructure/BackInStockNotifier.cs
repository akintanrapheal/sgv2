using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Emails customers who asked to be notified when an out-of-stock product is back. Periodic sweep
/// (mirrors <see cref="LowStockAlertService"/>): for each product with pending requests, if its
/// combined available stock across active branches is now &gt; 0, email each requester once and stamp
/// <see cref="Models.Domain.BackInStockRequest.NotifiedAt"/>. Only marks as notified when the email
/// actually sends, so requests survive an unconfigured SMTP and are retried next sweep.
/// </summary>
public class BackInStockNotifier : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    private readonly IServiceProvider _sp;
    private readonly ILogger<BackInStockNotifier> _logger;

    public BackInStockNotifier(IServiceProvider sp, ILogger<BackInStockNotifier> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Back-in-stock sweep failed."); }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var pending = await db.BackInStockRequests
            .Where(r => r.NotifiedAt == null)
            .Include(r => r.Product)
            .ToListAsync(ct);
        if (pending.Count == 0) return;

        var productIds = pending.Select(r => r.ProductId).Distinct().ToList();
        var activeStoreIds = await db.Stores.Where(s => s.IsActive).Select(s => s.Id).ToListAsync(ct);

        // Combined available (on-hand − reserved) per product across active branches.
        var availByProduct = (await db.StoreInventories
                .Where(si => productIds.Contains(si.ProductId) && activeStoreIds.Contains(si.StoreId))
                .GroupBy(si => si.ProductId)
                .Select(g => new { ProductId = g.Key, Avail = g.Sum(si => si.QuantityOnHand - si.QuantityReserved) })
                .ToListAsync(ct))
            .ToDictionary(x => x.ProductId, x => x.Avail);

        var email = scope.ServiceProvider.GetRequiredService<IEmailService>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var baseUrl = (config["App:BaseUrl"] ?? "").TrimEnd('/');
        var subject = await settings.GetAsync("email.back_in_stock.subject", "Good news — it's back in stock");
        var intro = await settings.GetAsync("email.back_in_stock.intro", "An item you wanted is available again. These pieces sell quickly, so don't wait.");
        var now = DateTime.UtcNow;
        int notified = 0;

        foreach (var group in pending.GroupBy(r => r.ProductId))
        {
            if (!availByProduct.TryGetValue(group.Key, out var avail) || avail <= 0) continue;

            var product = group.First().Product;
            if (product is null || !product.IsActive) continue;

            var link = string.IsNullOrEmpty(baseUrl) ? null : $"{baseUrl}/products/{product.Slug}";
            var cta = link != null
                ? $@"<p style=""margin:24px 0;""><a href=""{link}"" style=""background:#0a0a0a;color:#fff;text-decoration:none;padding:12px 28px;display:inline-block;font-size:13px;letter-spacing:1px;text-transform:uppercase;"">Shop now</a></p>"
                : "<p>Visit our website to order before it sells out again.</p>";
            var name = System.Net.WebUtility.HtmlEncode(product.Name);
            var body = $@"
                <h2 style=""font-size:18px;margin:0 0 12px;"">{System.Net.WebUtility.HtmlEncode(subject)}</h2>
                <p><strong>{name}</strong> is back in stock.</p>
                <p>{System.Net.WebUtility.HtmlEncode(intro)}</p>
                {cta}";

            foreach (var req in group)
            {
                if (await email.SendAsync(req.Email, subject, body, ct: ct))
                {
                    req.NotifiedAt = now;
                    notified++;
                }
            }
        }

        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
        if (notified > 0) _logger.LogInformation("Back-in-stock: notified {Count} customer(s).", notified);
    }
}
