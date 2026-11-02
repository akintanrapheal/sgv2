using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services.Marketing;

namespace SterlingLams.Web.Areas.Marketing.Controllers;

/// <summary>Every customer from the website back office, surfaced as a marketing contact — the
/// registered accounts, guest-checkout shells and old-POS imports in the Users table. Read-only:
/// a list with live subscription status + a per-contact profile (conversions, activity timeline,
/// email engagement). Newsletter-only emails with no account are counted but reachable through the
/// Newsletter audience.</summary>
public class ContactsController : MarketingAreaController
{
    private readonly ApplicationDbContext _db;
    private readonly IMarketingService _marketing;
    public ContactsController(ApplicationDbContext db, IMarketingService marketing)
    {
        _db = db;
        _marketing = marketing;
    }

    // Accounts that belong to staff, not customers — kept out of the marketing contact list.
    private static readonly string[] StaffRoles =
        { "Admin", "Owner", "Developer", "Operations", "Sales", "Inventory", "Social Media", "Marketing" };

    private async Task<List<string>> StaffUserIdsAsync()
    {
        var staffRoleIds = await _db.Roles.Where(r => r.Name != null && StaffRoles.Contains(r.Name))
            .Select(r => r.Id).ToListAsync();
        return await _db.UserRoles.Where(ur => staffRoleIds.Contains(ur.RoleId))
            .Select(ur => ur.UserId).Distinct().ToListAsync();
    }

    public async Task<IActionResult> Index(string? q, int page = 1)
    {
        ViewData["Title"] = "Contacts";
        const int pageSize = 25;
        if (page < 1) page = 1;
        q = q?.Trim();

        var staffIds = await StaffUserIdsAsync();
        var customers = _db.Users.AsNoTracking()
            .Where(u => u.Email != null && !staffIds.Contains(u.Id));

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.ToLower();
            customers = customers.Where(u =>
                u.Email!.ToLower().Contains(term) ||
                (u.FirstName + " " + u.LastName).ToLower().Contains(term) ||
                (u.PhoneNumber != null && u.PhoneNumber.Contains(term)));
        }

        // Overview tiles (unfiltered by search, so they describe the whole base).
        var allCustomers = _db.Users.AsNoTracking().Where(u => u.Email != null && !staffIds.Contains(u.Id));
        ViewBag.TotalContacts = await allCustomers.CountAsync();
        ViewBag.EmailSubs = await allCustomers
            .Where(u => !_db.MarketingSuppressions.Any(s => s.Email == u.Email!.ToLower())).CountAsync();
        ViewBag.PhoneSubs = await allCustomers.CountAsync(u => u.PhoneNumber != null && u.PhoneNumber != "");
        // Newsletter sign-ups that never became an account.
        ViewBag.NewsletterOnly = await _db.NewsletterSubscribers.AsNoTracking()
            .Where(n => !_db.Users.Any(u => u.Email!.ToLower() == n.Email.ToLower())).CountAsync();

        var total = await customers.CountAsync();
        ViewBag.Page = page;
        ViewBag.TotalPages = (int)Math.Ceiling(total / (double)pageSize);
        ViewBag.Total = total;
        ViewBag.Query = q;

        var rows = await customers
            .OrderByDescending(u => u.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(u => new ContactRow(
                u.Id, u.FirstName + " " + u.LastName, u.Email!, u.PhoneNumber, u.IsGuest, u.CreatedAt,
                !_db.MarketingSuppressions.Any(s => s.Email == u.Email!.ToLower())))
            .ToListAsync();
        return View(rows);
    }

    public record ContactRow(string Id, string Name, string Email, string? Phone, bool IsGuest,
        DateTime CreatedAt, bool EmailSubscribed);

    public record TimelineEvent(DateTime At, string Icon, string Title, string? Detail);

    public async Task<IActionResult> Detail(string id)
    {
        var u = await _db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (u == null) return NotFound();
        ViewData["Title"] = string.IsNullOrWhiteSpace(u.FullName) ? u.Email ?? "Contact" : u.FullName;
        ViewBag.User = u;

        var emailLower = (u.Email ?? "").ToLower();

        // Conversions — lifetime paid orders.
        var paid = await _db.Orders.AsNoTracking()
            .Where(o => o.UserId == id && o.IsPaid)
            .Select(o => new { o.Total, o.PaidAt }).ToListAsync();
        ViewBag.Conversions = paid.Count;
        ViewBag.Revenue = paid.Sum(x => x.Total);

        // All orders (for the timeline + recent list).
        var orders = await _db.Orders.AsNoTracking()
            .Where(o => o.UserId == id)
            .OrderByDescending(o => o.CreatedAt).Take(50)
            .Select(o => new { o.OrderNumber, o.Total, o.CreatedAt, o.PaidAt, o.IsPaid, o.Channel })
            .ToListAsync();
        ViewBag.Orders = orders;

        // Latest delivery address on record.
        ViewBag.Address = await _db.Orders.AsNoTracking()
            .Where(o => o.UserId == id && o.DeliveryAddress != null)
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => o.DeliveryAddress)
            .FirstOrDefaultAsync();

        // Email engagement — campaign sends to this contact.
        var campaignEmails = await _db.CampaignRecipients.AsNoTracking()
            .Where(r => (r.UserId == id || r.Email.ToLower() == emailLower) && r.SentAt != null)
            .OrderByDescending(r => r.SentAt)
            .Join(_db.Campaigns, r => r.CampaignId, c => c.Id, (r, c) => new {
                c.Subject, r.SentAt, r.OpenedAt, r.ClickedAt })
            .Take(50).ToListAsync();
        ViewBag.CampaignEmails = campaignEmails;

        var automationEmails = await _db.AutomationRuns.AsNoTracking()
            .Where(r => (r.UserId == id || r.Email.ToLower() == emailLower) && r.SentAt != null)
            .OrderByDescending(r => r.SentAt)
            .Join(_db.Automations, r => r.AutomationId, a => a.Id, (r, a) => new { a.Name, r.SentAt })
            .Take(50).ToListAsync();

        var carts = await _db.AbandonedCarts.AsNoTracking()
            .Where(c => c.Email.ToLower() == emailLower)
            .Select(c => new { c.Subtotal, c.ItemCount, c.CreatedAt, c.RecoveredAt }).ToListAsync();

        var newsletter = await _db.NewsletterSubscribers.AsNoTracking()
            .Where(n => n.Email.ToLower() == emailLower)
            .Select(n => (DateTime?)n.CreatedAt).FirstOrDefaultAsync();

        ViewBag.Unsubscribed = await _db.MarketingSuppressions.AnyAsync(s => s.Email == emailLower);

        // Build a merged activity timeline (most recent first).
        var tl = new List<TimelineEvent>();
        foreach (var o in orders)
        {
            if (o.IsPaid && o.PaidAt != null)
                tl.Add(new TimelineEvent(o.PaidAt.Value, "paid", "Order paid", $"{o.OrderNumber} · ₦{o.Total:N0}"));
            tl.Add(new TimelineEvent(o.CreatedAt, "order", "Order placed", $"{o.OrderNumber} · {o.Channel}"));
        }
        foreach (var c in carts)
        {
            if (c.RecoveredAt != null)
                tl.Add(new TimelineEvent(c.RecoveredAt.Value, "recovered", "Cart recovered", $"₦{c.Subtotal:N0}"));
            tl.Add(new TimelineEvent(c.CreatedAt, "cart", "Cart abandoned", $"{c.ItemCount} item(s) · ₦{c.Subtotal:N0}"));
        }
        foreach (var e in campaignEmails)
        {
            if (e.ClickedAt != null) tl.Add(new TimelineEvent(e.ClickedAt.Value, "click", "Clicked email", e.Subject));
            else if (e.OpenedAt != null) tl.Add(new TimelineEvent(e.OpenedAt.Value, "open", "Opened email", e.Subject));
            if (e.SentAt != null) tl.Add(new TimelineEvent(e.SentAt.Value, "email", "Received email", e.Subject));
        }
        foreach (var e in automationEmails)
            if (e.SentAt != null) tl.Add(new TimelineEvent(e.SentAt.Value, "email", "Received email", e.Name));
        if (newsletter != null)
            tl.Add(new TimelineEvent(newsletter.Value, "subscribe", "Subscribed to newsletter", null));
        tl.Add(new TimelineEvent(u.CreatedAt, "created", u.IsGuest ? "Guest contact created" : "Account created", null));

        ViewBag.Timeline = tl.OrderByDescending(e => e.At).Take(60).ToList();
        return View();
    }
}
