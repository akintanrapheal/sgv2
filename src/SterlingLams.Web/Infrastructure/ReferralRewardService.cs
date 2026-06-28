using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Rewards refer-a-friend referrals: when a referred customer places their first PAID order, both
/// the referrer and the new customer get loyalty points (settings-driven), once. Poll-based, so it
/// needs no hooks in the checkout/payment paths.
/// </summary>
public class ReferralRewardService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<ReferralRewardService> _logger;

    public ReferralRewardService(IServiceProvider sp, ILogger<ReferralRewardService> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(35), stoppingToken); } catch { }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Referral reward sweep failed."); }
            try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); } catch { }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        if (!await settings.GetBoolAsync("referral.enabled", true)) return;

        var pending = await db.Referrals.Where(r => r.Status == ReferralStatus.Pending).Take(100).ToListAsync(ct);
        if (pending.Count == 0) return;

        var referrerPoints = await settings.GetIntAsync("referral.referrer_points", 100);
        var refereePoints = await settings.GetIntAsync("referral.referee_points", 50);
        var loyalty = scope.ServiceProvider.GetRequiredService<ILoyaltyService>();

        var rewarded = 0;
        foreach (var r in pending)
        {
            // Qualify on the referee's first paid order.
            var order = await db.Orders.AsNoTracking()
                .Where(o => o.UserId == r.RefereeUserId && o.IsPaid)
                .OrderBy(o => o.PaidAt).FirstOrDefaultAsync(ct);
            if (order == null) continue;

            if (referrerPoints > 0) await loyalty.AdjustAsync(r.ReferrerUserId, referrerPoints, $"Referral reward (referred a friend)");
            if (refereePoints > 0) await loyalty.AdjustAsync(r.RefereeUserId, refereePoints, $"Welcome bonus (referred by a friend)");

            r.Status = ReferralStatus.Rewarded;
            r.QualifyingOrderId = order.Id;
            r.ReferrerPoints = referrerPoints;
            r.RefereePoints = refereePoints;
            r.RewardedAt = DateTime.UtcNow;
            rewarded++;
        }
        if (rewarded > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Referral sweep: rewarded {Count} referral(s).", rewarded);
        }
    }
}
