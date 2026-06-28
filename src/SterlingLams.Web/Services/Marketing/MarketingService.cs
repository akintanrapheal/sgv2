using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services.Marketing;

public record AudienceRecipient(string Email, string? Name, string? UserId);

public interface IMarketingService
{
    /// <summary>Resolves a campaign's audience to a deduped recipient list, excluding suppressed
    /// (unsubscribed) and blank emails.</summary>
    Task<List<AudienceRecipient>> ResolveAudienceAsync(Campaign c, CancellationToken ct = default);

    Task<int> EstimateCountAsync(Campaign c, CancellationToken ct = default);

    /// <summary>Tamper-proof unsubscribe token (the email, data-protected) for email links.</summary>
    string MakeUnsubscribeToken(string email);
    string? ReadUnsubscribeToken(string token);

    Task SuppressAsync(string email, string? reason, CancellationToken ct = default);

    string Normalize(string? email);
}

public class MarketingService : IMarketingService
{
    private readonly ApplicationDbContext _db;
    private readonly IDataProtector _protector;

    public MarketingService(ApplicationDbContext db, IDataProtectionProvider dp)
    {
        _db = db;
        _protector = dp.CreateProtector("Marketing.Unsubscribe.v1");
    }

    public string Normalize(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    public string MakeUnsubscribeToken(string email) => _protector.Protect(Normalize(email));
    public string? ReadUnsubscribeToken(string token)
    {
        try { return _protector.Unprotect(token); } catch { return null; }
    }

    public async Task SuppressAsync(string email, string? reason, CancellationToken ct = default)
    {
        var norm = Normalize(email);
        if (string.IsNullOrEmpty(norm)) return;
        if (await _db.MarketingSuppressions.AnyAsync(s => s.Email == norm, ct)) return;
        _db.MarketingSuppressions.Add(new MarketingSuppression { Email = norm, Reason = reason });
        await _db.SaveChangesAsync(ct);
    }

    public async Task<int> EstimateCountAsync(Campaign c, CancellationToken ct = default)
        => (await ResolveAudienceAsync(c, ct)).Count;

    public async Task<List<AudienceRecipient>> ResolveAudienceAsync(Campaign c, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var raw = new List<AudienceRecipient>();

        switch (c.Audience)
        {
            case CampaignAudience.NewsletterSubscribers:
                raw = await _db.NewsletterSubscribers.AsNoTracking()
                    .Select(n => new AudienceRecipient(n.Email, null, null)).ToListAsync(ct);
                break;

            case CampaignAudience.AllCustomers:
                raw = await PaidBuyersQuery()
                    .Select(g => new AudienceRecipient(g.Email, g.Name, g.UserId)).ToListAsync(ct);
                break;

            case CampaignAudience.RecentBuyers:
            {
                var cutoff = now.AddDays(-(c.AudienceDays ?? 30));
                raw = await PaidBuyersQuery()
                    .Where(g => g.Last >= cutoff)
                    .Select(g => new AudienceRecipient(g.Email, g.Name, g.UserId)).ToListAsync(ct);
                break;
            }

            case CampaignAudience.LapsedCustomers:
            {
                var cutoff = now.AddDays(-(c.AudienceDays ?? 90));
                raw = await PaidBuyersQuery()
                    .Where(g => g.Last < cutoff)
                    .Select(g => new AudienceRecipient(g.Email, g.Name, g.UserId)).ToListAsync(ct);
                break;
            }

            case CampaignAudience.HighValue:
            {
                var min = c.AudienceMinSpend ?? 0m;
                raw = await PaidBuyersQuery()
                    .Where(g => g.Spend >= min)
                    .Select(g => new AudienceRecipient(g.Email, g.Name, g.UserId)).ToListAsync(ct);
                break;
            }

            case CampaignAudience.ByState:
            {
                var state = (c.AudienceState ?? "").Trim().ToLower();
                raw = await _db.Orders.AsNoTracking()
                    .Where(o => o.IsPaid && o.User != null && o.User.Email != null
                        && o.DeliveryAddress != null && o.DeliveryAddress.State.ToLower() == state)
                    .Select(o => new AudienceRecipient(o.User!.Email!, o.User.FullName, o.UserId))
                    .ToListAsync(ct);
                break;
            }

            case CampaignAudience.NeverOrdered:
            {
                var customerRoleId = await _db.Roles.Where(r => r.Name == "Customer").Select(r => r.Id).FirstOrDefaultAsync(ct);
                var buyerIds = _db.Orders.Select(o => o.UserId);
                raw = await _db.Users.AsNoTracking()
                    .Where(u => !u.IsGuest && u.Email != null
                        && _db.UserRoles.Any(ur => ur.UserId == u.Id && ur.RoleId == customerRoleId)
                        && !buyerIds.Contains(u.Id))
                    .Select(u => new AudienceRecipient(u.Email!, u.FullName, u.Id))
                    .ToListAsync(ct);
                break;
            }
        }

        // Suppression list (unsubscribed).
        var suppressed = (await _db.MarketingSuppressions.AsNoTracking().Select(s => s.Email).ToListAsync(ct))
            .ToHashSet();

        var seen = new HashSet<string>();
        var result = new List<AudienceRecipient>();
        foreach (var r in raw)
        {
            var norm = Normalize(r.Email);
            if (string.IsNullOrEmpty(norm) || !norm.Contains('@')) continue;
            if (suppressed.Contains(norm)) continue;
            if (!seen.Add(norm)) continue;
            result.Add(r with { Email = r.Email.Trim() });
        }
        return result;
    }

    private record BuyerRow(string Email, string? Name, string? UserId, DateTime Last, decimal Spend);

    /// <summary>One row per paying customer with their last-order date + lifetime paid spend.</summary>
    private IQueryable<BuyerRow> PaidBuyersQuery() =>
        _db.Orders.AsNoTracking()
            .Where(o => o.IsPaid && o.User != null && o.User.Email != null)
            .GroupBy(o => new { o.UserId, o.User!.Email, o.User.FullName })
            .Select(g => new BuyerRow(
                g.Key.Email!, g.Key.FullName, g.Key.UserId,
                g.Max(o => o.CreatedAt), g.Sum(o => o.Total)));
}
