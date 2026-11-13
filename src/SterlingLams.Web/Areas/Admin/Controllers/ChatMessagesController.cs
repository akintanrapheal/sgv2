using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Admin.Controllers;

/// <summary>Admin → Chat Messages: the live-chat inbox. Shoppers chat from the storefront Help widget;
/// staff read each conversation and reply (delivered to the shopper's widget on its next poll). A chat
/// auto-ends after 30 min of shopper silence, after which the shopper is invited to leave a star review,
/// shown here.</summary>
public class ChatMessagesController : AdminBaseController
{
    protected override string Section => "ChatMessages";

    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private const int PageSize = 30;

    public ChatMessagesController(ApplicationDbContext db, UserManager<ApplicationUser> users)
    {
        _db = db; _users = users;
    }

    public async Task<IActionResult> Index(string status = "", string q = "", int page = 1)
    {
        ViewData["Title"] = "Chat Messages";

        var query = _db.ChatConversations.AsQueryable();
        if (status == "open") query = query.Where(c => c.StaffUnread > 0);
        else if (status == "done") query = query.Where(c => c.StaffUnread == 0);
        else if (status == "reviewed") query = query.Where(c => c.Rating != null);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var ids = _db.ChatMessages
                .Where(m => EF.Functions.ILike(m.Body, $"%{q}%"))
                .Select(m => m.ConversationId);
            query = query.Where(c =>
                ids.Contains(c.Id) ||
                (c.CustomerName != null && EF.Functions.ILike(c.CustomerName, $"%{q}%")) ||
                (c.ContactEmail != null && EF.Functions.ILike(c.ContactEmail, $"%{q}%")) ||
                (c.ContactPhone != null && EF.Functions.ILike(c.ContactPhone, $"%{q}%")));
        }

        if (page < 1) page = 1;
        var total = await query.CountAsync();
        var rows = await query
            .OrderByDescending(c => c.StaffUnread > 0)   // needs-reply first
            .ThenByDescending(c => c.LastMessageAt)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .ToListAsync();

        return View(new ChatInboxVm
        {
            Status = status, Q = q, Page = page,
            TotalPages = (int)Math.Ceiling(total / (double)PageSize), TotalRows = total,
            UnreadConversations = await _db.ChatConversations.CountAsync(c => c.StaffUnread > 0),
            Rows = rows
        });
    }

    public async Task<IActionResult> Detail(int id)
    {
        var conv = await _db.ChatConversations.FirstOrDefaultAsync(c => c.Id == id);
        if (conv == null) return RedirectToAction(nameof(Index));

        var msgs = await _db.ChatMessages.Where(m => m.ConversationId == id)
            .OrderBy(m => m.Id).ToListAsync();

        if (conv.StaffUnread != 0) { conv.StaffUnread = 0; await _db.SaveChangesAsync(); }  // viewing = read

        ViewData["Title"] = "Chat";
        return View(new ChatThreadVm { Conversation = conv, Messages = msgs });
    }

    // Live poll for an open thread — new messages since `after`, so the agent sees incoming lines without
    // reloading. Viewing clears the unread badge.
    [HttpGet]
    public async Task<IActionResult> NewSince(int id, int after = 0)
    {
        var rows = await _db.ChatMessages
            .Where(m => m.ConversationId == id && m.Id > after)
            .OrderBy(m => m.Id).ToListAsync();

        if (rows.Any(m => !m.FromStaff))
            await _db.ChatConversations.Where(c => c.Id == id && c.StaffUnread > 0)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.StaffUnread, 0));

        var ended = await _db.ChatConversations.Where(c => c.Id == id).Select(c => c.Ended).FirstOrDefaultAsync();
        return Json(new
        {
            ended,
            messages = rows.Select(m => new
            {
                id = m.Id,
                body = m.Body,
                fromStaff = m.FromStaff,
                at = SterlingLams.Web.Services.ReportCalendar.ToLocal(m.CreatedAt).ToString("HH:mm")
            })
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reply(int id, string body)
    {
        body = (body ?? "").Trim();
        var conv = await _db.ChatConversations.FirstOrDefaultAsync(c => c.Id == id);
        if (conv == null) return RedirectToAction(nameof(Index));
        if (body.Length == 0) return RedirectToAction(nameof(Detail), new { id });
        if (body.Length > 2000) body = body[..2000];

        var me = await _users.GetUserAsync(User);
        var senderName = !string.IsNullOrWhiteSpace(me?.FirstName) ? me!.FirstName : "Sterlin Glams";

        _db.ChatMessages.Add(new ChatMessage
        {
            ConversationId = id, CreatedAt = DateTime.UtcNow, FromStaff = true,
            Body = body, SenderName = senderName, SeenByCustomer = false
        });
        conv.LastMessageAt = DateTime.UtcNow;
        conv.LastBody = body;
        conv.LastFromStaff = true;
        conv.StaffUnread = 0;
        // A staff reply re-opens a chat the shopper may have let lapse, so they can see the answer + reply.
        if (conv.Ended && conv.EndedReason == "timeout") { conv.Ended = false; conv.EndedAt = null; conv.EndedReason = null; }
        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkRead(int id, string returnUrl = "")
    {
        await _db.ChatConversations.Where(c => c.Id == id && c.StaffUnread > 0)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.StaffUnread, 0));
        return LocalRedirect(string.IsNullOrEmpty(returnUrl) ? Url.Action(nameof(Index))! : returnUrl);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> End(int id)
    {
        var conv = await _db.ChatConversations.FirstOrDefaultAsync(c => c.Id == id);
        if (conv != null && !conv.Ended)
        {
            conv.Ended = true; conv.EndedAt = DateTime.UtcNow; conv.EndedReason = "staff";
            conv.StaffUnread = 0;
            await _db.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Detail), new { id });
    }

    public class ChatInboxVm
    {
        public string Status = "", Q = "";
        public int Page, TotalPages, TotalRows, UnreadConversations;
        public List<ChatConversation> Rows = new();
    }

    public class ChatThreadVm
    {
        public ChatConversation Conversation = null!;
        public List<ChatMessage> Messages = new();
    }
}
