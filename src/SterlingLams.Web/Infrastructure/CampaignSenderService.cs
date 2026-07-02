using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using SterlingLams.Web.Services.Marketing;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Sends marketing campaigns in the background: picks up due (scheduled) or in-progress campaigns,
/// materialises their recipient list once, then sends each email via the shared EmailService with
/// an unsubscribe footer. Resumable + idempotent — only ever sends a recipient still marked Pending,
/// so a restart mid-send never double-sends a completed recipient.
/// </summary>
public class CampaignSenderService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly IConfiguration _config;
    private readonly ILogger<CampaignSenderService> _logger;

    private const int BatchSize = 50;

    public CampaignSenderService(IServiceProvider sp, IConfiguration config, ILogger<CampaignSenderService> logger)
    {
        _sp = sp;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small initial delay so the app finishes starting before the first sweep.
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); } catch { }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Campaign sender tick failed."); }
            try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch { }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = DateTime.UtcNow;

        // One campaign per tick: a resumable in-flight one first, else the next due scheduled one.
        var campaign = await db.Campaigns
            .Where(c => c.Status == CampaignStatus.Sending
                     || (c.Status == CampaignStatus.Scheduled && c.ScheduledAt != null && c.ScheduledAt <= now))
            .OrderBy(c => c.ScheduledAt ?? c.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (campaign == null) return;

        var marketing = scope.ServiceProvider.GetRequiredService<IMarketingService>();
        var email = scope.ServiceProvider.GetRequiredService<IEmailService>();

        // Move Scheduled → Sending and materialise recipients exactly once.
        if (campaign.Status == CampaignStatus.Scheduled)
        {
            campaign.Status = CampaignStatus.Sending;
            campaign.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
        }

        var hasRecipients = await db.CampaignRecipients.AnyAsync(r => r.CampaignId == campaign.Id, ct);
        if (!hasRecipients)
        {
            var audience = await marketing.ResolveAudienceAsync(campaign, ct);
            foreach (var a in audience)
                db.CampaignRecipients.Add(new CampaignRecipient
                {
                    CampaignId = campaign.Id, Email = a.Email, Name = a.Name, UserId = a.UserId,
                    Status = CampaignRecipientStatus.Pending
                });
            campaign.RecipientCount = audience.Count;
            await db.SaveChangesAsync(ct);

            if (audience.Count == 0)
            {
                campaign.Status = CampaignStatus.Sent;
                campaign.SentAt = now;
                await db.SaveChangesAsync(ct);
                _logger.LogInformation("Campaign {Id} '{Name}' had no recipients.", campaign.Id, campaign.Name);
                return;
            }
        }

        var baseUrl = (_config["App:BaseUrl"] ?? "").TrimEnd('/');
        var pending = await db.CampaignRecipients
            .Where(r => r.CampaignId == campaign.Id && r.Status == CampaignRecipientStatus.Pending)
            .OrderBy(r => r.Id).Take(BatchSize).ToListAsync(ct);

        foreach (var r in pending)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var bodyHtml = campaign.BodyHtml;
                if (campaign.CouponEnabled && campaign.CouponValue > 0)
                {
                    var code = await marketing.MintCouponAsync(campaign.CouponType, campaign.CouponValue,
                        campaign.CouponExpiryDays, campaign.CouponMinOrder, $"Campaign: {campaign.Name}", ct);
                    bodyHtml = Services.Marketing.MarketingService.ApplyCoupon(bodyHtml, code);
                }
                var body = bodyHtml + UnsubscribeFooter(baseUrl, marketing, r.Email);
                // Self-hosted open/click tracking: rewrite links + append a 1×1 pixel keyed to this recipient.
                if (!string.IsNullOrEmpty(baseUrl))
                    body = Services.Marketing.MarketingService.InjectTracking(body, baseUrl, marketing.MakeTrackToken(r.Id));
                var ok = await email.SendAsync(r.Email, campaign.Subject, body, r.Name, ct);
                r.Status = ok ? CampaignRecipientStatus.Sent : CampaignRecipientStatus.Failed;
                r.SentAt = ok ? DateTime.UtcNow : null;
                if (ok) campaign.SentCount++; else { campaign.FailedCount++; r.Error = "send returned false"; }
            }
            catch (Exception ex)
            {
                r.Status = CampaignRecipientStatus.Failed;
                r.Error = ex.Message.Length > 280 ? ex.Message[..280] : ex.Message;
                campaign.FailedCount++;
            }
        }
        campaign.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        // Done when nothing is left pending.
        var remaining = await db.CampaignRecipients
            .CountAsync(r => r.CampaignId == campaign.Id && r.Status == CampaignRecipientStatus.Pending, ct);
        if (remaining == 0)
        {
            campaign.Status = CampaignStatus.Sent;
            campaign.SentAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Campaign {Id} '{Name}' sent: {Sent} ok, {Failed} failed.",
                campaign.Id, campaign.Name, campaign.SentCount, campaign.FailedCount);
        }
    }

    private static string UnsubscribeFooter(string baseUrl, IMarketingService marketing, string email)
    {
        if (string.IsNullOrEmpty(baseUrl)) return "";
        var token = Uri.EscapeDataString(marketing.MakeUnsubscribeToken(email));
        var url = $"{baseUrl}/unsubscribe?t={token}";
        return $"<p style=\"margin-top:24px;font-size:11px;color:#9ca3af;text-align:center\">" +
               $"You're receiving this because you shopped with or subscribed to Sterlin Glams. " +
               $"<a href=\"{url}\" style=\"color:#9ca3af;text-decoration:underline\">Unsubscribe</a>.</p>";
    }
}
