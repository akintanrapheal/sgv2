using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Proactively reaches out to a customer whose online order is placed but still UNPAID, a while after
/// checkout and before the unpaid auto-cancel (<see cref="ReservationSweeper"/>), to ask if they're having
/// trouble with payment — recovering the sale. Two channels, once per order:
///  • a friendly chat message dropped into the shopper's live-chat thread (pops up with the unread dot if
///    they're still on the site and signed in — the widget adopts a conversation keyed to their account), and
///  • an email (reaches them even if they've left the site), inviting them to reply / chat / finish the order.
/// Gated by <c>chat.payment_outreach_enabled</c>; timing by <c>chat.payment_outreach_minutes</c>.
/// </summary>
public class UnpaidOrderOutreachService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private const int DefaultDelayMinutes = 20;

    private readonly IServiceProvider _sp;
    private readonly ILogger<UnpaidOrderOutreachService> _log;

    public UnpaidOrderOutreachService(IServiceProvider sp, ILogger<UnpaidOrderOutreachService> log)
    {
        _sp = sp; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "Unpaid-order outreach sweep failed."); }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var email = scope.ServiceProvider.GetRequiredService<IEmailService>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        if (!await settings.GetBoolAsync("chat.payment_outreach_enabled", true)) return;

        var delayMin = (int)await settings.GetDecimalAsync("chat.payment_outreach_minutes", DefaultDelayMinutes);
        if (delayMin < 1) delayMin = DefaultDelayMinutes;
        var cancelMin = (int)await settings.GetDecimalAsync("order.reservation_timeout_minutes", 60);
        if (cancelMin < 1) cancelMin = 60;

        var now = DateTime.UtcNow;
        var youngerThan = now.AddMinutes(-delayMin);      // old enough to nudge
        var olderThan = now.AddMinutes(-cancelMin);        // not already past the auto-cancel window

        var orders = await db.Orders
            .Where(o => o.Channel == OrderChannel.Online && !o.IsPaid && o.Status == OrderStatus.Pending
                        && o.PaymentReminderAt == null && o.PlacedByStaffUserId == null
                        && o.CreatedAt <= youngerThan && o.CreatedAt > olderThan)
            .OrderBy(o => o.CreatedAt).Take(50).ToListAsync(ct);
        if (orders.Count == 0) return;

        var baseUrl = (config["App:BaseUrl"] ?? "").TrimEnd('/');

        foreach (var order in orders)
        {
            var user = string.IsNullOrEmpty(order.UserId) ? null
                : await db.Users.FirstOrDefaultAsync(u => u.Id == order.UserId, ct);
            var name = !string.IsNullOrWhiteSpace(order.ContactName) ? order.ContactName!.Split(' ')[0]
                       : !string.IsNullOrWhiteSpace(user?.FirstName) ? user!.FirstName : "there";
            var to = user?.Email;

            try { await PostChatOutreachAsync(db, order, name, user?.Email, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Chat outreach failed for order {Order}.", order.OrderNumber); }

            if (!string.IsNullOrWhiteSpace(to))
            {
                try { await SendEmailAsync(email, to!, name, order, baseUrl); }
                catch (Exception ex) { _log.LogWarning(ex, "Outreach email failed for order {Order}.", order.OrderNumber); }
            }

            order.PaymentReminderAt = now;   // send at most once
        }

        await db.SaveChangesAsync(ct);
        _log.LogInformation("Sent payment-help outreach for {Count} unpaid order(s).", orders.Count);
    }

    private static async Task PostChatOutreachAsync(ApplicationDbContext db, Order order, string name, string? emailAddr, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(order.UserId)) return;   // no account to key the chat to

        var now = DateTime.UtcNow;
        var conv = await db.ChatConversations
            .Where(c => c.UserId == order.UserId && !c.Ended)
            .OrderByDescending(c => c.LastMessageAt).FirstOrDefaultAsync(ct);
        if (conv == null)
        {
            conv = new ChatConversation
            {
                Key = Guid.NewGuid().ToString("N"), UserId = order.UserId,
                CreatedAt = now, LastMessageAt = now, LastCustomerAt = now,
                CustomerName = order.ContactName, ContactPhone = order.ContactPhone, ContactEmail = emailAddr,
            };
            db.ChatConversations.Add(conv);
        }

        var body = $"Hi {name} 👋 We noticed your order {order.OrderNumber} hasn't been completed yet. " +
                   "Are you having any trouble with payment, or anything else we can help with? " +
                   "Just reply here and our team will help you right away.";
        conv.Messages.Add(new ChatMessage
        {
            Conversation = conv, CreatedAt = now, FromStaff = true, IsAutomated = true,
            SenderName = "Sterlin Glams", Body = body, SeenByCustomer = false   // delivered to the widget on next poll
        });
        conv.LastMessageAt = now;
        conv.LastBody = body;
        conv.LastFromStaff = true;
        // StaffUnread is NOT bumped — this is an outbound message; it flags only when the shopper replies.
    }

    private static async Task SendEmailAsync(IEmailService email, string to, string name, Order order, string baseUrl)
    {
        string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        var shopHref = string.IsNullOrEmpty(baseUrl) ? "#" : baseUrl + "/";
        var html =
            $"<p style=\"font-size:15px\">Hi {Enc(name)},</p>" +
            $"<p style=\"font-size:15px\">We noticed your order <strong>{Enc(order.OrderNumber)}</strong> " +
            $"(₦{order.Total:N0}) is still awaiting payment. Did you run into any trouble completing it?</p>" +
            "<p style=\"font-size:15px\">We're here to help — just reply to this email or chat with us on the " +
            "website and we'll sort it out right away.</p>" +
            $"<p style=\"margin:20px 0\"><a href=\"{shopHref}\" style=\"display:inline-block;background:#ed028b;color:#fff;" +
            "text-decoration:none;padding:11px 22px;border-radius:8px;font-weight:600\">Return to complete your order →</a></p>" +
            "<p style=\"font-size:13px;color:#888\">If you've already paid, please ignore this message — thank you!</p>";

        await email.SendAsync(to, "Need help completing your order? — Sterlin Glams", html, name);
    }
}
