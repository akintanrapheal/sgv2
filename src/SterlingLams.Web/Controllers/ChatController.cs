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
    private readonly IR2Storage _r2;
    private readonly ILogger<ChatController> _log;

    public ChatController(ApplicationDbContext db, UserManager<ApplicationUser> users,
        IEmailService email, ISettingsService settings, IR2Storage r2, ILogger<ChatController> log)
    {
        _db = db; _users = users; _email = email; _settings = settings; _r2 = r2; _log = log;
    }

    private void SetCookie(string key) => Response.Cookies.Append(CookieName, key, new CookieOptions
    {
        HttpOnly = true, IsEssential = true, SameSite = SameSiteMode.Lax,
        Secure = Request.IsHttps, Expires = DateTimeOffset.UtcNow.AddDays(60), Path = "/"
    });

    private async Task<ChatConversation?> FindAsync()
    {
        var key = Request.Cookies[CookieName];
        if (!string.IsNullOrWhiteSpace(key) && key!.Length <= 40)
        {
            var byKey = await _db.ChatConversations.FirstOrDefaultAsync(c => c.Key == key);
            if (byKey != null) return byKey;
        }

        // No cookie thread yet, but a signed-in shopper may have a conversation we started for them (e.g. a
        // proactive "need help paying?" outreach keyed to their account). Adopt it so the widget shows it.
        if (User?.Identity?.IsAuthenticated == true)
        {
            var uid = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (!string.IsNullOrEmpty(uid))
            {
                var byUser = await _db.ChatConversations
                    .Where(c => c.UserId == uid && !c.Ended)
                    .OrderByDescending(c => c.LastMessageAt)
                    .FirstOrDefaultAsync();
                if (byUser != null) { SetCookie(byUser.Key); return byUser; }
            }
        }
        return null;
    }

    private static string? Trim(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? null : (s!.Trim().Length > max ? s.Trim()[..max] : s.Trim());

    // Resolve (or start) the shopper's thread and record their identity. A message after an ended chat
    // starts a fresh one. Returns the conversation and whether staff were caught up (so an alert pings once
    // per new wave). Shared by the text, image-upload and quick-reply-topic endpoints.
    private async Task<(ChatConversation conv, bool alertStaff)> ResolveConvAsync(string? name, string? email, string? phone)
    {
        var now = DateTime.UtcNow;
        var conv = await FindAsync();
        if (conv != null && conv.Ended) conv = null;
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

        var alertStaff = conv.StaffUnread == 0;

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
        return (conv, alertStaff);
    }

    // Append a shopper message (text and/or image), bump unread and alert staff once per wave.
    private async Task<int?> AppendCustomerAsync(string body, string? imageUrl, string? name, string? email, string? phone)
    {
        var (conv, alertStaff) = await ResolveConvAsync(name, email, phone);
        var now = DateTime.UtcNow;
        var msg = new ChatMessage
        {
            Conversation = conv, CreatedAt = now, FromStaff = false,
            Body = body, ImageUrl = imageUrl, SeenByCustomer = true
        };
        conv.Messages.Add(msg);
        conv.LastMessageAt = now;
        conv.LastCustomerAt = now;
        conv.StaffUnread += 1;
        conv.LastBody = imageUrl != null && body.Length == 0 ? "📷 Photo" : body;
        conv.LastFromStaff = false;

        try { await _db.SaveChangesAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to save chat message."); return null; }

        if (alertStaff)
        {
            try { await SendStaffAlertAsync(conv, imageUrl != null && body.Length == 0 ? "📷 Sent a photo" : body); }
            catch (Exception ex) { _log.LogWarning(ex, "Chat staff alert failed."); }
        }
        return msg.Id;
    }

    // Shopper picked a quick-reply topic/issue. Log their selection + a plain-text auto-reply so customer
    // care sees the full journey (which topic, then the shopper's own explanation). The widget shows its own
    // rich reply, so we return the ids and it skips re-fetching these. Issue topics ping staff; info topics
    // (order tracking / delivery / sizing) are logged for context only.
    [HttpPost("topic"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Topic(string key, string? name = null, string? email = null, string? phone = null)
    {
        var t = Infrastructure.ChatTopics.Get(key);
        if (t == null) return Json(new { ok = false });

        var (conv, alertStaff) = await ResolveConvAsync(name, email, phone);
        var now = DateTime.UtcNow;
        var pick = new ChatMessage { Conversation = conv, CreatedAt = now, FromStaff = false, Body = t.Label, SeenByCustomer = true };
        var reply = new ChatMessage { Conversation = conv, CreatedAt = now.AddMilliseconds(1), FromStaff = true,
            IsAutomated = true, SenderName = "Auto-reply", Body = t.AutoReply, SeenByCustomer = true };
        conv.Messages.Add(pick);
        conv.Messages.Add(reply);
        conv.LastMessageAt = now;
        conv.LastCustomerAt = now;
        conv.StaffUnread += 1;   // the selection shows as needing attention in the inbox
        conv.LastBody = t.Label;
        conv.LastFromStaff = false;

        try { await _db.SaveChangesAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to save chat topic."); return Json(new { ok = false }); }

        if (alertStaff && t.Alert)
        {
            try { await SendStaffAlertAsync(conv, t.Label); }
            catch (Exception ex) { _log.LogWarning(ex, "Chat topic alert failed."); }
        }
        return Json(new { ok = true, lastId = Math.Max(pick.Id, reply.Id) });
    }

    [HttpPost("message"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Message(string body, string? channel = null,
        string? name = null, string? email = null, string? phone = null)
    {
        body = (body ?? "").Trim();
        if (body.Length == 0) return Json(new { ok = false });
        if (body.Length > 2000) body = body[..2000];

        var id = await AppendCustomerAsync(body, null, name, email, phone);
        return id == null ? Json(new { ok = false }) : Json(new { ok = true, id });
    }

    // Shopper shares a photo. The file is validated by its real signature (magic bytes), size-capped and
    // stored RAW in R2 — never decoded with ImageSharp — so the decoder advisories can't be reached here.
    [HttpPost("upload"), ValidateAntiForgeryToken, RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<IActionResult> Upload(IFormFile file, string? name = null, string? email = null, string? phone = null)
    {
        var url = await StoreImageAsync(file);
        if (url == null) return Json(new { ok = false, error = "Please attach a JPG, PNG, GIF or WebP image under 5 MB." });

        var id = await AppendCustomerAsync("", url, name, email, phone);
        return id == null ? Json(new { ok = false }) : Json(new { ok = true, id, url });
    }

    // Validate + store a chat image. Returns the raw R2 URL, or null if it isn't a valid image / R2 is off.
    private async Task<string?> StoreImageAsync(IFormFile? file)
    {
        if (file == null || file.Length == 0 || file.Length > Infrastructure.ChatImageValidator.MaxBytes) return null;
        if (!await _r2.IsConfiguredAsync()) return null;

        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            await file.CopyToAsync(ms);
            bytes = ms.ToArray();
        }
        var sniff = Infrastructure.ChatImageValidator.Sniff(bytes);
        if (sniff == null) return null;   // not a real JPEG/PNG/GIF/WebP

        var key = $"chat/{Guid.NewGuid():N}.{sniff.Value.ext}";
        return await _r2.PutAsync(key, bytes, sniff.Value.contentType);
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
                imageUrl = m.ImageUrl,
                fromStaff = m.FromStaff,
                isAutomated = m.IsAutomated,
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
