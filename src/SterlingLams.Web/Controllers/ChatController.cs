using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Controllers;

/// <summary>Storefront live-chat back-end for the Help widget. Shoppers chat without signing in — a
/// durable cookie (sg_chat) threads the conversation. Staff reply from Admin → Chat Messages; the widget
/// polls <see cref="Thread"/> for replies. A chat auto-ends after 30 min with no reply from the shopper,
/// then the widget invites a star review. Writes are best-effort — chat must never break the page.</summary>
[Route("chat")]
public class ChatController : Controller
{
    private const string CookieName = "sg_chat";
    public const int TimeoutMinutes = 30;

    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly IEmailService _email;
    private readonly ISettingsService _settings;
    private readonly ILogger<ChatController> _log;

    public ChatController(ApplicationDbContext db, UserManager<ApplicationUser> users,
        IEmailService email, ISettingsService settings, ILogger<ChatController> log)
    {
        _db = db; _users = users; _email = email; _settings = settings; _log = log;
    }

    private void SetCookie(string key) => Response.Cookies.Append(CookieName, key, new CookieOptions
    {
        HttpOnly = true, IsEssential = true, SameSite = SameSiteMode.Lax,
        Secure = Request.IsHttps, Expires = DateTimeOffset.UtcNow.AddDays(60), Path = "/"
    });

    private async Task<ChatConversation?> FindAsync()
    {
        var key = Request.Cookies[CookieName];
        if (string.IsNullOrWhiteSpace(key) || key.Length > 40) return null;
        return await _db.ChatConversations.FirstOrDefaultAsync(c => c.Key == key);
    }

    [HttpPost("message"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Message(string body, string? channel = null,
        string? name = null, string? email = null, string? phone = null)
    {
        body = (body ?? "").Trim();
        if (body.Length == 0) return Json(new { ok = false });
        if (body.Length > 2000) body = body[..2000];

        string? Trim(string? s, int max) =>
            string.IsNullOrWhiteSpace(s) ? null : (s!.Trim().Length > max ? s.Trim()[..max] : s.Trim());

        var now = DateTime.UtcNow;
        // Resolve the thread — a fresh one if there's no cookie or the previous chat already ended.
        var conv = await FindAsync();
        if (conv != null && conv.Ended) conv = null;
        var isNew = conv == null;
        if (conv == null)
        {
            var key = Guid.NewGuid().ToString("N");
            SetCookie(key);
            conv = new ChatConversation
            {
                Key = key, CreatedAt = now, LastMessageAt = now, LastCustomerAt = now,
                FirstPath = Trim(Request.Headers.Referer.ToString(), 300) ?? Trim(Request.Path.Value, 300),
                IpAddress = Trim(HttpContext.Connection.RemoteIpAddress?.ToString(), 64),
                DeviceType = Trim(Infrastructure.OrderAttributionMiddleware.DeviceFromUserAgent(
                                 Request.Headers.UserAgent.ToString()), 40),
            };
            _db.ChatConversations.Add(conv);
        }

        var alertStaff = conv.StaffUnread == 0;   // staff were caught up → ping once for this new wave

        // Identity: prefer what the shopper typed, fall back to their signed-in account.
        conv.CustomerName = Trim(name, 120) ?? conv.CustomerName;
        conv.ContactEmail = Trim(email, 256) ?? conv.ContactEmail;
        conv.ContactPhone = Trim(phone, 40) ?? conv.ContactPhone;
        if (User?.Identity?.IsAuthenticated == true)
        {
            var u = await _users.GetUserAsync(User);
            if (u != null)
            {
                conv.UserId ??= u.Id;
                conv.CustomerName ??= string.IsNullOrWhiteSpace(u.FullName) ? null : u.FullName;
                conv.ContactEmail ??= u.Email;
                conv.ContactPhone ??= u.PhoneNumber;
            }
        }

        var msg = new ChatMessage
        {
            Conversation = conv, CreatedAt = now, FromStaff = false, Body = body, SeenByCustomer = true
        };
        conv.Messages.Add(msg);
        conv.LastMessageAt = now;
        conv.LastCustomerAt = now;
        conv.StaffUnread += 1;
        conv.LastBody = body;
        conv.LastFromStaff = false;

        try { await _db.SaveChangesAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to save chat message."); return Json(new { ok = false }); }

        if (alertStaff)
        {
            try { await SendStaffAlertAsync(conv, body); }
            catch (Exception ex) { _log.LogWarning(ex, "Chat staff alert failed."); }
        }

        return Json(new { ok = true, id = msg.Id });
    }

    // New messages since `after` (both sides) so the transcript rebuilds; also reports whether the chat
    // has ended (auto-ends here if the shopper's been silent past the timeout) and whether it's reviewed.
    [HttpGet("thread")]
    public async Task<IActionResult> Thread(int after = 0)
    {
        var conv = await FindAsync();
        if (conv == null) return Json(new { messages = Array.Empty<object>(), ended = false, reviewed = false });

        if (!conv.Ended && conv.LastCustomerAt < DateTime.UtcNow.AddMinutes(-TimeoutMinutes))
        {
            conv.Ended = true; conv.EndedAt = DateTime.UtcNow; conv.EndedReason = "timeout";
            try { await _db.SaveChangesAsync(); } catch { }
        }

        var rows = await _db.ChatMessages
            .Where(m => m.ConversationId == conv.Id && m.Id > after)
            .OrderBy(m => m.Id).ToListAsync();

        var toDeliver = rows.Where(m => m.FromStaff && !m.SeenByCustomer).ToList();
        if (toDeliver.Count > 0)
        {
            foreach (var m in toDeliver) m.SeenByCustomer = true;
            try { await _db.SaveChangesAsync(); } catch { }
        }

        return Json(new
        {
            ended = conv.Ended,
            reviewed = conv.Rating != null,
            messages = rows.Select(m => new
            {
                id = m.Id,
                body = m.Body,
                fromStaff = m.FromStaff,
                name = m.FromStaff ? (m.SenderName ?? "Sterlin Glams") : null,
                at = ReportCalendar.ToLocal(m.CreatedAt).ToString("HH:mm")
            })
        });
    }

    // Post-chat star review (1–5) + optional comment. Ends the chat if it wasn't already.
    [HttpPost("review"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Review(int rating, string? comment = null)
    {
        var conv = await FindAsync();
        if (conv == null) return Json(new { ok = false });
        if (rating < 1) rating = 1; if (rating > 5) rating = 5;

        conv.Rating = rating;
        conv.ReviewComment = string.IsNullOrWhiteSpace(comment) ? null
            : (comment.Trim().Length > 1000 ? comment.Trim()[..1000] : comment.Trim());
        conv.ReviewedAt = DateTime.UtcNow;
        if (!conv.Ended) { conv.Ended = true; conv.EndedAt = DateTime.UtcNow; conv.EndedReason = "reviewed"; }

        try { await _db.SaveChangesAsync(); } catch (Exception ex) { _log.LogWarning(ex, "Chat review save failed."); return Json(new { ok = false }); }
        return Json(new { ok = true });
    }

    private async Task SendStaffAlertAsync(ChatConversation conv, string body)
    {
        var to = await _settings.GetAsync("general.contact_email");
        if (string.IsNullOrWhiteSpace(to)) return;

        string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        var who = !string.IsNullOrWhiteSpace(conv.CustomerName) ? conv.CustomerName
                  : !string.IsNullOrWhiteSpace(conv.ContactEmail) ? conv.ContactEmail : "A customer";
        var contact = string.Join(" · ", new[] { conv.ContactEmail, conv.ContactPhone }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        var html =
            $"<p style=\"font-size:15px\">{Enc(who)} sent a live chat message on the website:</p>" +
            $"<blockquote style=\"margin:12px 0;padding:10px 14px;background:#f7f7f8;border-left:3px solid #ed028b;border-radius:6px\">{Enc(body)}</blockquote>" +
            (string.IsNullOrWhiteSpace(contact) ? "" : $"<p style=\"font-size:13px;color:#666\">Contact: {Enc(contact)}</p>") +
            "<p style=\"font-size:13px;color:#666\">Open <strong>Admin → Chat Messages</strong> to reply.</p>";

        await _email.SendAsync(to, "New live chat message — Sterlin Glams", html);
    }
}
