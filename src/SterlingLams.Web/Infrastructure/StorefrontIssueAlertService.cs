using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Backs the Customer Issues monitor's alerting: every few minutes it counts recent issues per bucket
/// (add-to-cart / payment / server error) over a rolling window and, if a bucket crosses its threshold,
/// emails (and optionally WhatsApps) the owner — then stays quiet for the rest of the window so it never
/// spams. Also purges issue records older than the retention setting so the table stays small. All
/// settings-gated and best-effort; a failure here never affects the storefront.
/// </summary>
public class StorefrontIssueAlertService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private readonly IServiceProvider _sp;
    private readonly ILogger<StorefrontIssueAlertService> _logger;

    public StorefrontIssueAlertService(IServiceProvider sp, ILogger<StorefrontIssueAlertService> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small startup delay so the app finishes booting / migrating first.
        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); } catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Customer-issues alert sweep failed."); }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private record Bucket(string Key, string Label, StorefrontIssueType Type, string ThresholdSetting, int ThresholdDefault, string LastAlertSetting);

    private static readonly Bucket[] Buckets =
    {
        new("cart",    "add-to-cart failures", StorefrontIssueType.AddToCart,   "issues.alert_threshold_cart",    10, "issues.alert_last_cart"),
        new("payment", "payment failures",     StorefrontIssueType.Payment,     "issues.alert_threshold_payment",  5, "issues.alert_last_payment"),
        new("errors",  "storefront errors",    StorefrontIssueType.ServerError, "issues.alert_threshold_errors",   5, "issues.alert_last_errors"),
    };

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // ── Retention purge (runs regardless of whether alerts are on) ──────────
        var retentionDays = Math.Max(7, await settings.GetIntAsync("issues.retention_days", 90));
        var purgeCutoff = DateTime.UtcNow.AddDays(-retentionDays);
        try { await db.StorefrontIssues.Where(i => i.CreatedAt < purgeCutoff).ExecuteDeleteAsync(ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Customer-issues purge failed."); }

        if (!await settings.GetBoolAsync("issues.alerts_enabled", true)) return;

        var windowMin = Math.Max(5, await settings.GetIntAsync("issues.alert_window_minutes", 60));
        var windowStart = DateTime.UtcNow.AddMinutes(-windowMin);
        var now = DateTime.UtcNow;

        var toSave = new Dictionary<string, string>();
        var fired = new List<(Bucket b, int count, int threshold)>();

        foreach (var b in Buckets)
        {
            var threshold = Math.Max(1, await settings.GetIntAsync(b.ThresholdSetting, b.ThresholdDefault));
            var count = await db.StorefrontIssues
                .CountAsync(i => i.Type == b.Type && i.CreatedAt >= windowStart, ct);
            if (count < threshold) continue;

            // Don't re-alert for the same bucket within the window (dedup a sustained spike to one ping).
            var lastStr = await settings.GetAsync(b.LastAlertSetting, "");
            if (DateTime.TryParse(lastStr, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var last)
                && last > now.AddMinutes(-windowMin))
                continue;

            fired.Add((b, count, threshold));
            toSave[b.LastAlertSetting] = now.ToString("o");
        }

        if (fired.Count == 0) return;

        await SendAlertsAsync(scope, settings, fired, windowMin, ct);
        if (toSave.Count > 0) await settings.SaveManyAsync(toSave);
    }

    private async Task SendAlertsAsync(IServiceScope scope, ISettingsService settings,
        List<(Bucket b, int count, int threshold)> fired, int windowMin, CancellationToken ct)
    {
        static string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        var lines = string.Join("", fired.Select(f =>
            $"<li><strong>{f.count}</strong> {Enc(f.b.Label)} in the last {windowMin} min (alert at {f.threshold}).</li>"));
        var subject = "Customer issues spike — " + string.Join(", ", fired.Select(f => $"{f.count} {f.b.Label}"));
        var body = $@"<h2 style=""font-size:18px;margin:0 0 8px;"">Customers are hitting problems</h2>
            <ul style=""font-size:14px;color:#374151;"">{lines}</ul>
            <p style=""font-size:14px;color:#57534e;"">Open <strong>Admin → Customer Issues</strong> to see the details and work on them.</p>";

        // Email recipients: the dedicated list, falling back to the general admin email.
        var emails = await settings.GetAsync("issues.alert_emails", "");
        if (string.IsNullOrWhiteSpace(emails)) emails = await settings.GetAsync("notifications.admin_email", "");
        var fromAlerts = await settings.GetAsync("email.from_alerts", "");
        var email = scope.ServiceProvider.GetRequiredService<IEmailService>();
        foreach (var addr in EmailRecipients.Split(emails))
        {
            try { await email.SendAsync(addr, subject, body, ct: ct, fromOverride: fromAlerts); }
            catch (Exception ex) { _logger.LogWarning(ex, "Customer-issues alert email failed."); }
        }

        // Optional WhatsApp (off by default).
        if (await settings.GetBoolAsync("issues.alert_whatsapp", false))
        {
            var numbers = await settings.GetAsync("issues.alert_whatsapp_numbers", "");
            var text = "⚠️ Sterlin Glams — customer issues spike: " +
                string.Join("; ", fired.Select(f => $"{f.count} {f.b.Label}")) +
                $" (last {windowMin} min). Check Admin → Customer Issues.";
            var wa = scope.ServiceProvider.GetRequiredService<IWhatsAppService>();
            foreach (var num in EmailRecipients.Split(numbers))
            {
                try { await wa.SendAsync(num, text, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "Customer-issues alert WhatsApp failed."); }
            }
        }

        _logger.LogInformation("Customer-issues alert sent: {Summary}",
            string.Join(", ", fired.Select(f => $"{f.count} {f.b.Key}")));
    }
}
