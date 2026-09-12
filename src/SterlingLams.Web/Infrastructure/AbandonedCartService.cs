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
            try { await LowStockSweepAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Cart low-stock sweep failed."); }
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

            var itemsHtml = await ItemsHtmlAsync(db, ab.ItemsJson, baseUrl, ct);
            var body = BuildBody(subject, intro, ab, baseUrl, coupon, discountPct, emailNo, maxSteps, itemsHtml);

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

    // Rebuild the product list (with thumbnails) from the cart snapshot for the recovery email.
    private sealed record CartSnap(int ProductId, int? VariantId, int Quantity);
    private static async Task<string> ItemsHtmlAsync(ApplicationDbContext db, string itemsJson, string baseUrl, CancellationToken ct)
    {
        List<CartSnap>? lines = null;
        try
        {
            lines = System.Text.Json.JsonSerializer.Deserialize<List<CartSnap>>(itemsJson,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { }
        if (lines == null || lines.Count == 0) return "";

        var pids = lines.Select(l => l.ProductId).Distinct().ToList();
        var prods = await db.Products.Where(p => pids.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, Img = p.Images.OrderByDescending(i => i.IsPrimary).Select(i => i.Url).FirstOrDefault() })
            .ToDictionaryAsync(x => x.Id, ct);

        var sb = new System.Text.StringBuilder(@"<table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""font-size:14px;border-collapse:collapse;margin:12px 0;"">");
        foreach (var l in lines)
        {
            if (!prods.TryGetValue(l.ProductId, out var p)) continue;
            var abs = string.IsNullOrWhiteSpace(p.Img) ? null
                : (p.Img.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? p.Img
                   : (string.IsNullOrEmpty(baseUrl) ? null : baseUrl + "/" + p.Img.TrimStart('/')));
            sb.Append($@"<tr><td style=""padding:8px 0;border-bottom:1px solid #f0efee;color:#374151;vertical-align:middle;"">{OrderEmailTemplate.Thumb(abs)}<strong style=""color:#1c1917;"">{System.Net.WebUtility.HtmlEncode(p.Name)}</strong> &times; {l.Quantity}</td></tr>");
        }
        sb.Append("</table>");
        return sb.ToString();
    }

    // ── Low-stock "selling fast" nudge ────────────────────────────────────────
    // For an open cart snapshot, if any item has dropped to the threshold or fewer left in stock
    // (others are buying it), send a one-time urgency email with a Buy-now link. Independent of the
    // time-based recovery sequence above; reset to re-fire only when the shopper changes their bag.
    private async Task LowStockSweepAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        if (!await settings.GetBoolAsync("notifications.cart_low_stock", true)) return;
        var threshold = await settings.GetIntAsync("notifications.cart_low_stock_threshold", 3);
        if (threshold <= 0) threshold = 3;
        // Only nudge a RECENTLY abandoned cart. A background sweep can't see a shopper's live session
        // bag, so a stale snapshot from days ago would otherwise email "your bag is almost gone" to
        // someone whose cart is long empty. Keep the window tight (default 24h; 0 = disabled).
        var maxHours = await settings.GetIntAsync("notifications.cart_low_stock_max_hours", 24);
        if (maxHours <= 0) return;

        var now = DateTime.UtcNow;
        var minAge = now - TimeSpan.FromHours(1);              // not while they're still actively shopping
        var oldest = now - TimeSpan.FromHours(maxHours);       // …and not a stale, days-old snapshot

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var candidates = await db.AbandonedCarts
            .Where(a => a.RecoveredAt == null && a.LowStockNotifiedAt == null
                     && a.CreatedAt < minAge && a.CreatedAt > oldest)
            .OrderBy(a => a.CreatedAt).Take(500).ToListAsync(ct);
        if (candidates.Count == 0) return;

        var email = scope.ServiceProvider.GetRequiredService<IEmailService>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var baseUrl = (config["App:BaseUrl"] ?? "").TrimEnd('/');
        var subject = await settings.GetAsync("email.cart_low_stock.subject", "⏳ Selling fast — your bag is almost gone");
        var intro = await settings.GetAsync("email.cart_low_stock.intro",
            "Great taste — and you're not the only one! Some pieces in your bag are down to their last few. Grab them now before they're gone for good.");
        var activeStoreIds = await db.Stores.Where(s => s.IsActive).Select(s => s.Id).ToListAsync(ct);
        int sent = 0;

        foreach (var ab in candidates)
        {
            var converted = await db.Orders.AnyAsync(o => o.User.Email == ab.Email && o.IsPaid && o.CreatedAt >= ab.CreatedAt, ct);
            if (converted) { ab.RecoveredAt = now; continue; }

            List<CartSnap>? lines;
            try
            {
                lines = System.Text.Json.JsonSerializer.Deserialize<List<CartSnap>>(ab.ItemsJson,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch { lines = null; }
            if (lines == null || lines.Count == 0) continue;

            var pids = lines.Select(l => l.ProductId).Distinct().ToList();
            var inv = await db.StoreInventories
                .Where(si => activeStoreIds.Contains(si.StoreId) && pids.Contains(si.ProductId))
                .Select(si => new { si.ProductId, si.ProductVariantId, Avail = si.QuantityOnHand - si.QuantityReserved })
                .ToListAsync(ct);
            int AvailFor(int pid, int? vid)
            {
                if (vid.HasValue)
                {
                    var hasOwn = inv.Any(i => i.ProductId == pid && i.ProductVariantId == vid);
                    if (hasOwn) return Math.Max(0, inv.Where(i => i.ProductId == pid && i.ProductVariantId == vid).Sum(i => i.Avail));
                    return Math.Max(0, inv.Where(i => i.ProductId == pid && i.ProductVariantId == null).Sum(i => i.Avail)); // pool fallback
                }
                return Math.Max(0, inv.Where(i => i.ProductId == pid).Sum(i => i.Avail));
            }
            var low = lines.Select(l => new { l.ProductId, Avail = AvailFor(l.ProductId, l.VariantId) })
                           .Where(x => x.Avail > 0 && x.Avail <= threshold).ToList();
            if (low.Count == 0) continue;

            var names = await db.Products.Where(p => low.Select(x => x.ProductId).Contains(p.Id))
                .Select(p => new { p.Id, p.Name, Img = p.Images.OrderByDescending(i => i.IsPrimary).Select(i => i.Url).FirstOrDefault() })
                .ToDictionaryAsync(x => x.Id, ct);

            var sb = new System.Text.StringBuilder(@"<table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""font-size:14px;border-collapse:collapse;margin:12px 0;"">");
            foreach (var x in low)
            {
                if (!names.TryGetValue(x.ProductId, out var p)) continue;
                var abs = string.IsNullOrWhiteSpace(p.Img) ? null
                    : (p.Img.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? p.Img
                       : (string.IsNullOrEmpty(baseUrl) ? null : baseUrl + "/" + p.Img.TrimStart('/')));
                sb.Append($@"<tr><td style=""padding:8px 0;border-bottom:1px solid #f0efee;color:#374151;vertical-align:middle;"">{OrderEmailTemplate.Thumb(abs)}<strong style=""color:#1c1917;"">{System.Net.WebUtility.HtmlEncode(p.Name)}</strong> &nbsp;<span style=""color:#dc2626;font-weight:700;"">Only {x.Avail} left!</span></td></tr>");
            }
            sb.Append("</table>");

            var body = BuildLowStockBody(subject, intro, ab, baseUrl, sb.ToString());
            if (await email.SendAsync(ab.Email, subject, body, ct: ct))
            {
                ab.LowStockNotifiedAt = now;
                sent++;
            }
        }

        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
        if (sent > 0) _logger.LogInformation("Cart low-stock: sent {Count} nudge email(s).", sent);
    }

    private static string BuildLowStockBody(string subject, string intro, AbandonedCart ab, string baseUrl, string itemsHtml)
    {
        string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);
        var link = string.IsNullOrEmpty(baseUrl) ? null : $"{baseUrl}/cart/recover?token={ab.Token}";
        var cta = link != null
            ? $@"<p style=""margin:24px 0;""><a href=""{link}"" style=""background:#0a0a0a;color:#fff;text-decoration:none;padding:12px 28px;display:inline-block;font-size:13px;letter-spacing:1px;text-transform:uppercase;"">Buy now</a></p>"
            : "<p>Return to our website to complete your order.</p>";
        return $@"
            <h2 style=""font-size:18px;margin:0 0 12px;"">{Enc(subject)}</h2>
            <p>{Enc(intro)}</p>
            {itemsHtml}
            {cta}
            <p style=""font-size:13px;color:#78716c;"">Stock is limited and moving fast — don't miss out.</p>";
    }

    private static string BuildBody(string subject, string intro, AbandonedCart ab, string baseUrl,
        string? coupon, int pct, int emailNo, int maxSteps, string itemsHtml)
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
            {itemsHtml}
            <p style=""color:#78716c;font-size:13px;"">{ab.ItemCount} item(s) · Subtotal ₦{ab.Subtotal:N0}</p>
            {couponBlock}
            {cta}
            <p style=""font-size:13px;color:#78716c;"">{footer}</p>";
    }
}
