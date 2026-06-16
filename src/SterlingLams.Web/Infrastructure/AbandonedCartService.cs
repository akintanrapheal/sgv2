using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Emails a recovery link to shoppers who reached checkout but didn't pay within the configured
/// delay. Periodic sweep (mirrors the other notifier services). Skips snapshots that have since
/// converted to a paid order, and only sends once per snapshot.
/// </summary>
public class AbandonedCartService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    private readonly IServiceProvider _sp;
    private readonly ILogger<AbandonedCartService> _logger;

    public AbandonedCartService(IServiceProvider sp, ILogger<AbandonedCartService> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Abandoned-cart sweep failed."); }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        if (!await settings.GetBoolAsync("notifications.abandoned_cart", true)) return;

        var hours = await settings.GetIntAsync("notifications.abandoned_cart_hours", 4);
        if (hours <= 0) hours = 4;
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(hours);

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var pending = await db.AbandonedCarts
            .Where(a => a.RecoveredAt == null && a.EmailedAt == null && a.CreatedAt < cutoff)
            .ToListAsync(ct);
        if (pending.Count == 0) return;

        var email = scope.ServiceProvider.GetRequiredService<IEmailService>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var baseUrl = (config["App:BaseUrl"] ?? "").TrimEnd('/');
        var now = DateTime.UtcNow;
        int sent = 0;

        foreach (var ab in pending)
        {
            // Did they end up buying after this snapshot? Then it's not abandoned — mark recovered.
            var converted = await db.Orders.AnyAsync(o => o.User.Email == ab.Email && o.IsPaid && o.CreatedAt >= ab.CreatedAt, ct);
            if (converted) { ab.RecoveredAt = now; continue; }

            var link = string.IsNullOrEmpty(baseUrl) ? null : $"{baseUrl}/cart/recover?token={ab.Token}";
            var cta = link != null
                ? $@"<p style=""margin:24px 0;""><a href=""{link}"" style=""background:#0a0a0a;color:#fff;text-decoration:none;padding:12px 28px;display:inline-block;font-size:13px;letter-spacing:1px;text-transform:uppercase;"">Complete your order</a></p>"
                : "<p>Return to our website to complete your order.</p>";
            var body = $@"
                <h2 style=""font-size:18px;margin:0 0 12px;"">You left something behind</h2>
                <p>You have {ab.ItemCount} item(s) (₦{ab.Subtotal:N0}) waiting in your bag. We've saved them for you.</p>
                {cta}
                <p style=""font-size:13px;color:#78716c;"">Stock is limited and these pieces sell quickly.</p>";

            if (await email.SendAsync(ab.Email, "You left something in your bag", body, ct: ct))
            {
                ab.EmailedAt = now;
                sent++;
            }
        }

        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
        if (sent > 0) _logger.LogInformation("Abandoned-cart: sent {Count} recovery email(s).", sent);
    }
}
