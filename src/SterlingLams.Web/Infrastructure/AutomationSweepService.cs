using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using SterlingLams.Web.Services.Marketing;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Runs marketing automations. Poll-based: every few minutes it (1) enrols newly-eligible customers
/// into each active automation — only events at/after the automation was activated, so it never
/// back-emails the whole history — and (2) sends any enrolments now due. One enrolment per customer
/// per automation (unique index), suppression honoured, unsubscribe footer added.
/// </summary>
public class AutomationSweepService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly IConfiguration _config;
    private readonly ILogger<AutomationSweepService> _logger;

    private const int EnrolCap = 500;   // per automation per tick
    private const int SendCap = 100;    // per tick

    public AutomationSweepService(IServiceProvider sp, IConfiguration config, ILogger<AutomationSweepService> logger)
    {
        _sp = sp;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken); } catch { }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Automation sweep failed."); }
            try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch { }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var marketing = scope.ServiceProvider.GetRequiredService<IMarketingService>();
        var now = DateTime.UtcNow;

        var automations = await db.Automations.Where(a => a.IsActive).ToListAsync(ct);
        foreach (var a in automations)
            await EnrolAsync(db, marketing, a, now, ct);

        await SendDueAsync(db, marketing, scope, now, ct);
    }

    private async Task EnrolAsync(ApplicationDbContext db, IMarketingService marketing, Automation a, DateTime now, CancellationToken ct)
    {
        var cutoff = a.ActivatedAt ?? a.CreatedAt;
        var existing = (await db.AutomationRuns.Where(r => r.AutomationId == a.Id).Select(r => r.Email).ToListAsync(ct)).ToHashSet();
        var suppressed = (await db.MarketingSuppressions.Select(s => s.Email).ToListAsync(ct)).ToHashSet();

        var candidates = new List<(string Email, string? Name, string? UserId, DateTime EventAt)>();

        switch (a.Trigger)
        {
            case AutomationTrigger.WelcomeNewCustomer:
            {
                // A customer = a real shopper account: not a guest shell, has an email, and is NOT
                // a staff member (customers aren't assigned a role on registration).
                var staffRoleIds = await StaffRoleIdsAsync(db, ct);
                var rows = await db.Users.AsNoTracking()
                    .Where(u => !u.IsGuest && u.Email != null && u.CreatedAt >= cutoff
                        && !db.UserRoles.Any(ur => ur.UserId == u.Id && staffRoleIds.Contains(ur.RoleId)))
                    .OrderBy(u => u.CreatedAt).Take(EnrolCap)
                    .Select(u => new { u.Email, u.FullName, u.Id, u.CreatedAt }).ToListAsync(ct);
                candidates.AddRange(rows.Select(r => (Email: r.Email!, Name: (string?)r.FullName, UserId: (string?)r.Id, EventAt: r.CreatedAt)));
                break;
            }
            case AutomationTrigger.PostPurchase:
            {
                var rows = await db.Orders.AsNoTracking()
                    .Where(o => o.IsPaid && o.PaidAt != null && o.PaidAt >= cutoff && o.User != null && o.User.Email != null)
                    .GroupBy(o => new { o.UserId, o.User!.Email, o.User.FullName })
                    .Select(g => new { g.Key.Email, g.Key.FullName, g.Key.UserId, First = g.Min(o => o.PaidAt!.Value) })
                    .OrderBy(x => x.First).Take(EnrolCap).ToListAsync(ct);
                candidates.AddRange(rows.Select(r => (Email: r.Email!, Name: (string?)r.FullName, UserId: (string?)r.UserId, EventAt: r.First)));
                break;
            }
            case AutomationTrigger.WinBackLapsed:
            {
                var lapsedBefore = now.AddDays(-a.WinBackDays);
                var floor = now.AddDays(-730); // don't chase ancient one-offs
                var rows = await db.Orders.AsNoTracking()
                    .Where(o => o.IsPaid && o.User != null && o.User.Email != null)
                    .GroupBy(o => new { o.UserId, o.User!.Email, o.User.FullName })
                    .Select(g => new { g.Key.Email, g.Key.FullName, g.Key.UserId, Last = g.Max(o => o.CreatedAt) })
                    .Where(x => x.Last <= lapsedBefore && x.Last >= floor)
                    .Take(EnrolCap).ToListAsync(ct);
                candidates.AddRange(rows.Select(r => (Email: r.Email!, Name: (string?)r.FullName, UserId: (string?)r.UserId, EventAt: now)));
                break;
            }
        }

        var added = 0;
        foreach (var c in candidates)
        {
            var norm = marketing.Normalize(c.Email);
            if (string.IsNullOrEmpty(norm) || !norm.Contains('@')) continue;
            if (existing.Contains(c.Email) || existing.Contains(norm)) continue;
            if (suppressed.Contains(norm)) continue;
            existing.Add(c.Email);
            db.AutomationRuns.Add(new AutomationRun
            {
                AutomationId = a.Id, Email = c.Email.Trim(), Name = c.Name, UserId = c.UserId,
                RunAt = c.EventAt.AddHours(a.DelayHours), Status = AutomationRunStatus.Pending
            });
            added++;
        }
        if (added > 0)
        {
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { db.ChangeTracker.Clear(); } // unique-index race — safe to drop
        }
    }

    private async Task SendDueAsync(ApplicationDbContext db, IMarketingService marketing, IServiceScope scope, DateTime now, CancellationToken ct)
    {
        var due = await db.AutomationRuns
            .Where(r => r.Status == AutomationRunStatus.Pending && r.RunAt <= now)
            .OrderBy(r => r.RunAt).Take(SendCap).ToListAsync(ct);
        if (due.Count == 0) return;

        var email = scope.ServiceProvider.GetRequiredService<IEmailService>();
        var baseUrl = (_config["App:BaseUrl"] ?? "").TrimEnd('/');
        var suppressed = (await db.MarketingSuppressions.Select(s => s.Email).ToListAsync(ct)).ToHashSet();
        var automations = await db.Automations.ToDictionaryAsync(a => a.Id, ct);

        foreach (var r in due)
        {
            if (ct.IsCancellationRequested) break;
            if (!automations.TryGetValue(r.AutomationId, out var a) || !a.IsActive)
            {
                r.Status = AutomationRunStatus.Skipped; continue;
            }
            if (suppressed.Contains(marketing.Normalize(r.Email)))
            {
                r.Status = AutomationRunStatus.Skipped; continue;
            }
            try
            {
                var bodyHtml = a.BodyHtml;
                if (a.CouponEnabled && a.CouponValue > 0)
                {
                    var code = await marketing.MintCouponAsync(a.CouponType, a.CouponValue,
                        a.CouponExpiryDays, a.CouponMinOrder, $"Automation: {a.Name}", ct);
                    bodyHtml = Services.Marketing.MarketingService.ApplyCoupon(bodyHtml, code);
                }
                var body = bodyHtml + Footer(baseUrl, marketing, r.Email);
                var ok = await email.SendAsync(r.Email, a.Subject, body, r.Name, ct);
                r.Status = ok ? AutomationRunStatus.Sent : AutomationRunStatus.Failed;
                r.SentAt = ok ? DateTime.UtcNow : null;
                if (ok) a.SentCount++; else r.Error = "send returned false";
            }
            catch (Exception ex)
            {
                r.Status = AutomationRunStatus.Failed;
                r.Error = ex.Message.Length > 280 ? ex.Message[..280] : ex.Message;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private static readonly string[] StaffRoleNames = { "Admin", "Operations", "Sales", "Inventory", "Social Media" };
    private static async Task<List<string>> StaffRoleIdsAsync(ApplicationDbContext db, CancellationToken ct) =>
        await db.Roles.Where(r => r.Name != null && StaffRoleNames.Contains(r.Name)).Select(r => r.Id).ToListAsync(ct);

    private static string Footer(string baseUrl, IMarketingService marketing, string email)
    {
        if (string.IsNullOrEmpty(baseUrl)) return "";
        var token = Uri.EscapeDataString(marketing.MakeUnsubscribeToken(email));
        var url = $"{baseUrl}/unsubscribe?t={token}";
        return $"<p style=\"margin-top:24px;font-size:11px;color:#9ca3af;text-align:center\">" +
               $"You're receiving this from Sterlin Glams. " +
               $"<a href=\"{url}\" style=\"color:#9ca3af;text-decoration:underline\">Unsubscribe</a>.</p>";
    }
}
