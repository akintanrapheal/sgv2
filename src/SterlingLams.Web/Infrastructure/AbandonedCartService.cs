using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using SterlingLams.Web.Services.Marketing;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Recovers abandoned checkouts with a multi-step reminder sequence: up to three emails at
/// increasing delays from abandonment (default 4h / 24h / 72h), with an optional escalating discount
/// on the later step(s). Periodic sweep (mirrors the other notifier services). Stops the sequence as
/// soon as the shopper pays or uses the recovery link; never re-emails a step; ignores ancient carts.
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

    // Don't start emailing carts older than this — avoids blasting a backlog when the sequence is
    // first enabled or after downtime. A full 3-step run (72h) sits comfortably inside this.
    private const int MaxAgeDays = 14;

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        if (!await settings.GetBoolAsync("notifications.abandoned_cart", true)) return;

        // Reminder schedule — delays (hours from abandonment). Later steps 0/≤previous = disabled.
        var h1 = await settings.GetIntAsync("notifications.abandoned_cart_hours", 4);
        if (h1 <= 0) h1 = 4;
        var h2 = await settings.GetIntAsync("notifications.abandoned_cart_hours_2", 24);
        var h3 = await settings.GetIntAsync("notifications.abandoned_cart_hours_3", 72);
        var delays = new List<int> { h1 };
        if (h2 > delays[^1]) delays.Add(h2);
        if (h3 > delays[^1]) delays.Add(h3);
        var maxSteps = delays.Count;

        // Escalating incentive: a unique % coupon from a chosen email number onward (0 = never).
        var discountPct = await settings.GetIntAsync("notifications.abandoned_cart_discount_pct", 0);
        var discountStep = await settings.GetIntAsync("notifications.abandoned_cart_discount_step", 3);
        var discountExpiry = await settings.GetIntAsync("notifications.abandoned_cart_discount_expiry_days", 7);

        var now = DateTime.UtcNow;
        var earliest = now - TimeSpan.FromHours(h1);
        var oldest = now - TimeSpan.FromDays(MaxAgeDays);

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var candidates = await db.AbandonedCarts
            .Where(a => a.RecoveredAt == null && a.RemindersSent < maxSteps
                     && a.CreatedAt < earliest && a.CreatedAt > oldest)
            .OrderBy(a => a.CreatedAt)
            .Take(500)
            .ToListAsync(ct);
        if (candidates.Count == 0) return;

        var email = scope.ServiceProvider.GetRequiredService<IEmailService>();
        var marketing = scope.ServiceProvider.GetRequiredService<IMarketingService>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var baseUrl = (config["App:BaseUrl"] ?? "").TrimEnd('/');
        var s1Subject = await settings.GetAsync("email.abandoned_cart.subject", "You left something in your bag");
        var s1Intro = await settings.GetAsync("email.abandoned_cart.intro", "You have items waiting in your bag — we've saved them for you.");
        int sent = 0;

        foreach (var ab in candidates)
        {
            // Did they buy after this snapshot? Then it's not abandoned — stop the sequence.
            var converted = await db.Orders.AnyAsync(o => o.User.Email == ab.Email && o.IsPaid && o.CreatedAt >= ab.CreatedAt, ct);
            if (converted) { ab.RecoveredAt = now; continue; }

            var stepIndex = ab.RemindersSent;               // 0-based next step
            if (stepIndex >= delays.Count) continue;
            if (now < ab.CreatedAt + TimeSpan.FromHours(delays[stepIndex])) continue; // not due yet

            var emailNo = stepIndex + 1;                    // 1-based
            var (subject, intro) = StepCopy(emailNo, s1Subject, s1Intro);

            string? coupon = null;
            if (discountPct > 0 && discountStep > 0 && emailNo >= discountStep)
                coupon = await marketing.MintCouponAsync(DiscountType.Percentage, discountPct, discountExpiry, null,
                    $"Cart recovery ({discountPct}% off)", ct);

            var body = BuildBody(subject, intro, ab, baseUrl, coupon, discountPct, emailNo, maxSteps);

            if (await email.SendAsync(ab.Email, subject, body, ct: ct))
            {
                ab.RemindersSent = emailNo;
                ab.EmailedAt = now;
                sent++;
            }
        }

        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
        if (sent > 0) _logger.LogInformation("Abandoned-cart: sent {Count} reminder email(s).", sent);
    }

    private static (string subject, string intro) StepCopy(int emailNo, string s1Subject, string s1Intro) => emailNo switch
    {
        1 => (s1Subject, s1Intro),
        2 => ("Still thinking it over?", "Your picks are still in your bag — complete your order before they sell out."),
        _ => ("Last chance for your bag", "This is a final reminder — your saved items may sell out soon."),
    };

    private static string BuildBody(string subject, string intro, AbandonedCart ab, string baseUrl,
        string? coupon, int pct, int emailNo, int maxSteps)
    {
        string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);
        var link = string.IsNullOrEmpty(baseUrl) ? null : $"{baseUrl}/cart/recover?token={ab.Token}";
        var cta = link != null
            ? $@"<p style=""margin:24px 0;""><a href=""{link}"" style=""background:#0a0a0a;color:#fff;text-decoration:none;padding:12px 28px;display:inline-block;font-size:13px;letter-spacing:1px;text-transform:uppercase;"">Complete your order</a></p>"
            : "<p>Return to our website to complete your order.</p>";
        var couponBlock = !string.IsNullOrEmpty(coupon)
            ? $@"<p style=""margin:16px 0;padding:12px 16px;background:#fdf2f8;border:1px dashed #ec4899;text-align:center;"">Here's <strong>{pct}% off</strong> to finish up — use code <strong style=""font-size:16px;letter-spacing:1px;"">{Enc(coupon)}</strong> at checkout.</p>"
            : "";
        var footer = emailNo >= maxSteps
            ? "This is our last reminder — your saved items may sell out soon."
            : "Stock is limited and these pieces sell quickly.";
        return $@"
            <h2 style=""font-size:18px;margin:0 0 12px;"">{Enc(subject)}</h2>
            <p>{Enc(intro)}</p>
            <p>{ab.ItemCount} item(s) · ₦{ab.Subtotal:N0}</p>
            {couponBlock}
            {cta}
            <p style=""font-size:13px;color:#78716c;"">{footer}</p>";
    }
}
