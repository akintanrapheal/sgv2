using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

public interface ILoyaltyService
{
    /// <summary>Awards loyalty points for a paid order, once. Safe to call from every paid-order
    /// path (callback, webhook, POS) — a unique index on the source order makes it idempotent.</summary>
    Task AccrueForOrderAsync(int orderId);

    /// <summary>Current points balance for a user (0 if they have no wallet yet).</summary>
    Task<int> GetBalanceAsync(string userId);
}

public class LoyaltyService : ILoyaltyService
{
    private readonly ApplicationDbContext _db;
    private readonly ISettingsService _settings;
    private readonly ILogger<LoyaltyService> _logger;

    private const decimal DefaultNairaPerPoint = 100m;

    public LoyaltyService(ApplicationDbContext db, ISettingsService settings, ILogger<LoyaltyService> logger)
    {
        _db = db;
        _settings = settings;
        _logger = logger;
    }

    public async Task<int> GetBalanceAsync(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return 0;
        return await _db.LoyaltyAccounts.Where(a => a.UserId == userId)
            .Select(a => (int?)a.PointsBalance).FirstOrDefaultAsync() ?? 0;
    }

    public async Task AccrueForOrderAsync(int orderId)
    {
        // Loyalty can be switched off entirely from Admin → Settings.
        if (!await _settings.GetBoolAsync("loyalty.enabled", true)) return;

        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
        if (order is null || !order.IsPaid) return;

        // POS sales credit the attached customer (the cashier is the UserId); online orders credit
        // the buyer. Walk-in POS sales with no customer earn nothing.
        var userId = order.Channel == OrderChannel.Pos ? order.CustomerUserId : order.UserId;
        if (string.IsNullOrEmpty(userId)) return;

        // Idempotent: an order can only ever award points once (also guarded by a unique index).
        if (await _db.PointsLedgerEntries.AnyAsync(p => p.OrderId == orderId)) return;

        // ₦ per point from settings (admin-tunable); guard against 0/negative.
        var nairaPerPoint = await _settings.GetDecimalAsync("loyalty.naira_per_point", DefaultNairaPerPoint);
        if (nairaPerPoint <= 0) nairaPerPoint = DefaultNairaPerPoint;

        var points = (int)Math.Floor(order.Total / nairaPerPoint);
        if (points <= 0) return;

        var now = DateTime.UtcNow;
        var account = await _db.LoyaltyAccounts.FirstOrDefaultAsync(a => a.UserId == userId);
        if (account is null)
        {
            account = new LoyaltyAccount { UserId = userId, CreatedAt = now, UpdatedAt = now };
            _db.LoyaltyAccounts.Add(account);
        }

        account.PointsBalance += points;
        account.UpdatedAt = now;
        account.Entries.Add(new PointsLedgerEntry
        {
            Points = points,
            Reason = $"Earned on order {order.OrderNumber}",
            OrderId = order.Id,
            CreatedAt = now
        });

        try
        {
            await _db.SaveChangesAsync();
            _logger.LogInformation("Awarded {Points} loyalty points to {UserId} for order {OrderNumber}.",
                points, userId, order.OrderNumber);
        }
        catch (DbUpdateException)
        {
            // A concurrent paid-order path already accrued for this order (unique OrderId index) —
            // safe to ignore; the points were awarded once.
            _db.ChangeTracker.Clear();
        }
    }
}
