using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Infrastructure;

public interface IFinanceReportService
{
    /// <summary>Builds and emails the finance summary for the configured window. Pass
    /// <paramref name="toOverride"/> to send a one-off test to a specific address.</summary>
    Task<(bool Ok, string Message)> SendAsync(string? toOverride = null);
}

public class FinanceReportService : IFinanceReportService
{
    private readonly ApplicationDbContext _db;
    private readonly ISettingsService _settings;
    private readonly IEmailService _email;

    public FinanceReportService(ApplicationDbContext db, ISettingsService settings, IEmailService email)
    {
        _db = db;
        _settings = settings;
        _email = email;
    }

    public async Task<(bool Ok, string Message)> SendAsync(string? toOverride = null)
    {
        var toRaw = toOverride ?? await _settings.GetAsync("finance.report_email_to", "");
        var recipients = toRaw.Split(new[] { ',', ';', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(e => new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(e))
            .Distinct().ToList();
        if (recipients.Count == 0) return (false, "No valid recipient emails configured.");

        var freq = (await _settings.GetAsync("finance.report_email_freq", "weekly")).ToLowerInvariant();
        var days = freq == "monthly" ? 30 : 7;
        var freqLabel = freq == "monthly" ? "Monthly" : "Weekly";

        var t = DateTime.UtcNow;
        var f = DateTime.SpecifyKind(t.Date.AddDays(-days), DateTimeKind.Utc);

        var paid = _db.Orders.Where(o => o.IsPaid && o.CreatedAt >= f && o.CreatedAt < t);
        var agg = await paid.GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Gross = g.Sum(o => o.Total), Delivery = g.Sum(o => o.DeliveryFee) })
            .FirstOrDefaultAsync();
        var refunds = await _db.Refunds.Where(r => r.CreatedAt >= f && r.CreatedAt < t).SumAsync(r => (decimal?)r.Amount) ?? 0;
        var posGross = await paid.Where(o => o.Channel == OrderChannel.Pos).SumAsync(o => (decimal?)o.Total) ?? 0;

        var gross = agg?.Gross ?? 0;
        var delivery = agg?.Delivery ?? 0;
        var count = agg?.Count ?? 0;
        var net = gross - refunds;

        string Money(decimal v) => "₦" + v.ToString("N0");
        string Row(string label, decimal v, string colour = "#1c1917") =>
            $"<tr><td style=\"padding:8px 0;color:#57534e;\">{label}</td><td style=\"padding:8px 0;text-align:right;font-weight:600;color:{colour};\">{Money(v)}</td></tr>";

        var body =
            $"<h2 style=\"font-size:18px;margin:0 0 4px;\">{freqLabel} finance summary</h2>"
            + $"<p style=\"color:#78716c;font-size:13px;margin:0 0 18px;\">{f:dd MMM yyyy} – {t:dd MMM yyyy}</p>"
            + "<table style=\"width:100%;border-collapse:collapse;font-size:14px;\">"
            + Row("Order revenue", gross - delivery)
            + Row("Logistics revenue", delivery)
            + Row("Total revenue", gross)
            + Row("Refunds", refunds, "#dc2626")
            + $"<tr><td style=\"padding:10px 0;border-top:1px solid #e7e5e4;font-weight:700;\">Net revenue</td><td style=\"padding:10px 0;border-top:1px solid #e7e5e4;text-align:right;font-weight:700;\">{Money(net)}</td></tr>"
            + "</table>"
            + $"<p style=\"color:#57534e;font-size:13px;margin:16px 0 0;\">{count} transaction(s) · POS {Money(posGross)} · Online {Money(gross - posGross)}</p>"
            + "<p style=\"color:#a8a29e;font-size:12px;margin:20px 0 0;\">Automated by the Finance module. Open the Finance dashboard for the full breakdown.</p>";

        var subject = $"Sterlin Glams — {freqLabel} finance summary ({f:dd MMM}–{t:dd MMM})";

        var sent = 0;
        foreach (var to in recipients)
            if (await _email.SendAsync(to, subject, body)) sent++;

        return sent > 0 ? (true, $"Sent to {sent} recipient(s).") : (false, "Could not send — check SMTP settings.");
    }
}

/// <summary>
/// Emails the finance summary on a cadence (weekly/monthly). Checks hourly and sends when the
/// configured interval has elapsed since the last send. Off by default (finance.report_email_enabled).
/// </summary>
public class FinanceReportScheduler : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private readonly IServiceProvider _sp;
    private readonly ILogger<FinanceReportScheduler> _logger;

    public FinanceReportScheduler(IServiceProvider sp, ILogger<FinanceReportScheduler> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "Finance report scheduler tick failed."); }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task TickAsync()
    {
        using var scope = _sp.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        if (!await settings.GetBoolAsync("finance.report_email_enabled", false)) return;

        var freq = (await settings.GetAsync("finance.report_email_freq", "weekly")).ToLowerInvariant();
        var interval = freq == "monthly" ? TimeSpan.FromDays(30) : TimeSpan.FromDays(7);

        var lastRaw = await settings.GetAsync("finance.report_last_sent", "");
        if (DateTime.TryParse(lastRaw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal, out var last)
            && DateTime.UtcNow - last < interval)
            return; // not due yet

        var svc = scope.ServiceProvider.GetRequiredService<IFinanceReportService>();
        var (ok, msg) = await svc.SendAsync();
        if (ok)
        {
            await settings.SaveManyAsync(new Dictionary<string, string>
            {
                ["finance.report_last_sent"] = DateTime.UtcNow.ToString("O")
            });
            _logger.LogInformation("Finance summary emailed: {Message}", msg);
        }
        else
        {
            _logger.LogWarning("Finance summary not sent: {Message}", msg);
        }
    }
}
