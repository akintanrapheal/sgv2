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

    /// <summary>Mints a unique single-use discount code (returns the code) for a per-recipient
    /// marketing coupon — max 1 use, expiring in <paramref name="expiryDays"/> days.</summary>
    Task<string> MintCouponAsync(DiscountType type, decimal value, int expiryDays, decimal? minOrder, string label, CancellationToken ct = default);
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

    private const string CouponAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public async Task<string> MintCouponAsync(DiscountType type, decimal value, int expiryDays, decimal? minOrder, string label, CancellationToken ct = default)
    {
        string code;
        do { code = "SG" + RandomBlock(8); } while (await _db.DiscountCodes.AnyAsync(d => d.Code == code, ct));
        _db.DiscountCodes.Add(new DiscountCode
        {
            Code = code,
            Description = string.IsNullOrWhiteSpace(label) ? "Marketing coupon" : (label.Length > 180 ? label[..180] : label),
            Type = type,
            Value = value,
            Scope = DiscountScope.EntireOrder,
            MinimumOrderAmount = minOrder,
            MaxUses = 1,
            MaxUsesPerCustomer = 1,
            IsActive = true,
            ExpiresAt = expiryDays > 0 ? DateTime.UtcNow.AddDays(expiryDays) : (DateTime?)null,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(ct);
        return code;
    }

    private static string RandomBlock(int len)
    {
        var sb = new System.Text.StringBuilder(len);
        Span<byte> buf = stackalloc byte[len];
        System.Security.Cryptography.RandomNumberGenerator.Fill(buf);
        foreach (var b in buf) sb.Append(CouponAlphabet[b % CouponAlphabet.Length]);
        return sb.ToString();
    }

    /// <summary>Injects a coupon code into an email body: replaces {{coupon}} tokens, or appends a
    /// styled line if the placeholder is absent. No-op when <paramref name="code"/> is null/empty.</summary>
    public static string ApplyCoupon(string body, string? code)
    {
        if (string.IsNullOrEmpty(code)) return body;
        if (body.Contains("{{coupon}}", StringComparison.OrdinalIgnoreCase))
            return System.Text.RegularExpressions.Regex.Replace(body, @"\{\{coupon\}\}", code,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return body + $"<p style=\"text-align:center;margin:18px 0\">Your code: " +
               $"<strong style=\"letter-spacing:1px;font-size:15px\">{code}</strong></p>";
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
        // A saved segment, when attached, overrides the inline audience definition.
        if (c.SegmentId is int sid)
        {
            var seg = await _db.Segments.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid, ct);
            if (seg != null)
                c = new Campaign { Audience = seg.Audience, AudienceDays = seg.Days, AudienceMinSpend = seg.MinSpend, AudienceState = seg.State };
        }

        var now = DateTime.UtcNow;
        var raw = new List<AudienceRecipient>();

        switch (c.Audience)
        {
            case CampaignAudience.NewsletterSubscribers:
                raw = await _db.NewsletterSubscribers.AsNoTracking()
                    .Select(n => new AudienceRecipient(n.Email, null, null)).ToListAsync(ct);
                break;

            case CampaignAudience.AllCustomers:
                raw = (await PaidBuyersQuery().ToListAsync(ct))
                    .Select(g => new AudienceRecipient(g.Email, g.Name, g.UserId)).ToList();
                break;

            case CampaignAudience.RecentBuyers:
            {
                // Filtering on the post-GroupBy aggregate (Last/Spend) can't be translated through the
                // BuyerRow projection, so materialise the grouped buyers then filter in memory.
                var cutoff = now.AddDays(-(c.AudienceDays ?? 30));
                raw = (await PaidBuyersQuery().ToListAsync(ct))
                    .Where(g => g.Last >= cutoff)
                    .Select(g => new AudienceRecipient(g.Email, g.Name, g.UserId)).ToList();
                break;
            }

            case CampaignAudience.LapsedCustomers:
            {
                var cutoff = now.AddDays(-(c.AudienceDays ?? 90));
                raw = (await PaidBuyersQuery().ToListAsync(ct))
                    .Where(g => g.Last < cutoff)
                    .Select(g => new AudienceRecipient(g.Email, g.Name, g.UserId)).ToList();
                break;
            }

            case CampaignAudience.HighValue:
            {
                var min = c.AudienceMinSpend ?? 0m;
                raw = (await PaidBuyersQuery().ToListAsync(ct))
                    .Where(g => g.Spend >= min)
                    .Select(g => new AudienceRecipient(g.Email, g.Name, g.UserId)).ToList();
                break;
            }

            case CampaignAudience.ByState:
            {
                var state = (c.AudienceState ?? "").Trim().ToLower();
                raw = await _db.Orders.AsNoTracking()
                    .Where(o => o.IsPaid && o.User != null && o.User.Email != null
                        && o.DeliveryAddress != null && o.DeliveryAddress.State.ToLower() == state)
                    .Select(o => new AudienceRecipient(o.User!.Email!, o.User!.FirstName + " " + o.User.LastName, o.UserId))
                    .ToListAsync(ct);
                break;
            }

            case CampaignAudience.NeverOrdered:
            {
                // Customers = non-guest accounts that aren't staff (no role is assigned on signup).
                var staffRoleNames = new[] { "Admin", "Operations", "Sales", "Inventory", "Social Media" };
                var staffRoleIds = await _db.Roles.Where(r => r.Name != null && staffRoleNames.Contains(r.Name))
                    .Select(r => r.Id).ToListAsync(ct);
                var buyerIds = _db.Orders.Select(o => o.UserId);
                raw = await _db.Users.AsNoTracking()
                    .Where(u => !u.IsGuest && u.Email != null
                        && !_db.UserRoles.Any(ur => ur.UserId == u.Id && staffRoleIds.Contains(ur.RoleId))
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
            // Group by mapped columns only — FullName is [NotMapped] and can't be translated;
            // build the display name from FirstName/LastName in the projection instead.
            .GroupBy(o => new { o.UserId, o.User!.Email, o.User.FirstName, o.User.LastName })
            .Select(g => new BuyerRow(
                g.Key.Email!, g.Key.FirstName + " " + g.Key.LastName, g.Key.UserId,
                g.Max(o => o.CreatedAt), g.Sum(o => o.Total)));
}
