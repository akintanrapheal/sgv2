using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

public record ReferralStats(string Code, int Referred, int Rewarded, int PointsEarned);

public interface IReferralService
{
    Task<bool> EnabledAsync();
    /// <summary>The user's own referral code, generating + persisting one on first use.</summary>
    Task<string> GetOrCreateCodeAsync(string userId);
    Task<string?> ResolveReferrerIdAsync(string code);
    /// <summary>Records a Pending referral if the code is valid, not self, and the referee isn't
    /// already referred. Safe + idempotent. Returns true if a referral was created.</summary>
    Task<bool> TryCreateReferralAsync(string refereeUserId, string code);
    Task<ReferralStats> StatsForAsync(string userId);
}

public class ReferralService : IReferralService
{
    private readonly ApplicationDbContext _db;
    private readonly ISettingsService _settings;
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public ReferralService(ApplicationDbContext db, ISettingsService settings)
    {
        _db = db;
        _settings = settings;
    }

    public Task<bool> EnabledAsync() => _settings.GetBoolAsync("referral.enabled", true);

    public async Task<string> GetOrCreateCodeAsync(string userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null) return string.Empty;
        if (!string.IsNullOrEmpty(user.ReferralCode)) return user.ReferralCode;

        string code;
        do { code = Generate(6); } while (await _db.Users.AnyAsync(u => u.ReferralCode == code));
        user.ReferralCode = code;
        await _db.SaveChangesAsync();
        return code;
    }

    public async Task<string?> ResolveReferrerIdAsync(string code)
    {
        var norm = (code ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(norm)) return null;
        return await _db.Users.Where(u => u.ReferralCode == norm).Select(u => u.Id).FirstOrDefaultAsync();
    }

    public async Task<bool> TryCreateReferralAsync(string refereeUserId, string code)
    {
        if (!await EnabledAsync()) return false;
        if (string.IsNullOrEmpty(refereeUserId)) return false;

        var referrerId = await ResolveReferrerIdAsync(code);
        if (referrerId == null || referrerId == refereeUserId) return false;          // invalid / self-referral
        if (await _db.Referrals.AnyAsync(r => r.RefereeUserId == refereeUserId)) return false; // already referred

        _db.Referrals.Add(new Referral
        {
            ReferrerUserId = referrerId,
            RefereeUserId = refereeUserId,
            Code = code.Trim().ToUpperInvariant(),
            Status = ReferralStatus.Pending
        });
        try { await _db.SaveChangesAsync(); return true; }
        catch (DbUpdateException) { _db.ChangeTracker.Clear(); return false; } // unique-index race
    }

    public async Task<ReferralStats> StatsForAsync(string userId)
    {
        var code = await GetOrCreateCodeAsync(userId);
        var rows = await _db.Referrals.AsNoTracking().Where(r => r.ReferrerUserId == userId).ToListAsync();
        return new ReferralStats(
            code,
            rows.Count,
            rows.Count(r => r.Status == ReferralStatus.Rewarded),
            rows.Where(r => r.Status == ReferralStatus.Rewarded).Sum(r => r.ReferrerPoints));
    }

    private static string Generate(int len)
    {
        var sb = new StringBuilder(len);
        Span<byte> buf = stackalloc byte[len];
        RandomNumberGenerator.Fill(buf);
        foreach (var b in buf) sb.Append(Alphabet[b % Alphabet.Length]);
        return sb.ToString();
    }
}
