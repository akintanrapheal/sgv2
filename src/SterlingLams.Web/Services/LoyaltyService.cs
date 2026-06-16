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

    /// <summary>Whether redeeming points at checkout is enabled.</summary>
    Task<bool> RedemptionEnabledAsync();

    /// <summary>₦ value of one point when redeemed.</summary>
    Task<decimal> PointValueAsync();

    /// <summary>Deducts the points earmarked on a paid order (Order.LoyaltyPointsRedeemed), once.
    /// Safe to call from every paid-order path — idempotent via Order.LoyaltyRedeemedAt.</summary>
    Task RedeemForOrderAsync(int orderId);

    /// <summary>Reverses loyalty on a fully-refunded order: claws back the points earned and returns
    /// the points redeemed. Once per order (idempotent via Order.LoyaltyReversedAt).</summary>
    Task ReverseForOrderAsync(int orderId);
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

    public async Task<bool> RedemptionEnabledAsync() =>
        await _settings.GetBoolAsync("loyalty.enabled", true)
        && await _settings.GetBoolAsync("loyalty.redemption_enabled", true);

    public async Task<decimal> PointValueAsync()
    {
        var v = await _settings.GetDecimalAsync("loyalty.point_value", 1m);
        return v <= 0 ? 1m : v;
    }

    public async Task RedeemForOrderAsync(int orderId)
    {
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
        if (order is null || order.LoyaltyPointsRedeemed <= 0) return;
        if (order.LoyaltyRedeemedAt != null) return; // already deducted (idempotent)

        var userId = order.Channel == OrderChannel.Pos ? order.CustomerUserId : order.UserId;
        if (string.IsNullOrEmpty(userId)) return;

        var account = await _db.LoyaltyAccounts.FirstOrDefaultAsync(a => a.UserId == userId);
        // Clamp to the balance actually available now (guards a concurrent spend between
        // earmark-at-checkout and deduct-at-payment); the shop never goes below zero.
        var toDeduct = Math.Min(order.LoyaltyPointsRedeemed, account?.PointsBalance ?? 0);
        var now = DateTime.UtcNow;
        order.LoyaltyRedeemedAt = now;

        if (account != null && toDeduct > 0)
        {
            account.PointsBalance -= toDeduct;
            account.UpdatedAt = now;
            account.Entries.Add(new PointsLedgerEntry
            {
                Points = -toDeduct,
                Reason = $"Redeemed on order {order.OrderNumber}",
                OrderId = null, // earn entry already holds this order's OrderId (unique); keep ref in Reason
                CreatedAt = now
            });
            if (toDeduct < order.LoyaltyPointsRedeemed)
                _logger.LogWarning("Order {OrderNumber} earmarked {Earmarked} pts but only {Deducted} were available.",
                    order.OrderNumber, order.LoyaltyPointsRedeemed, toDeduct);
        }

        await _db.SaveChangesAsync();
    }

    public async Task ReverseForOrderAsync(int orderId)
    {
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
        if (order is null || order.LoyaltyReversedAt != null) return; // idempotent

        var now = DateTime.UtcNow;
        order.LoyaltyReversedAt = now;

        var userId = order.Channel == OrderChannel.Pos ? order.CustomerUserId : order.UserId;
        var account = string.IsNullOrEmpty(userId)
            ? null
            : await _db.LoyaltyAccounts.FirstOrDefaultAsync(a => a.UserId == userId);

        if (account != null)
        {
            // Points earned on this order (the accrual entry carries OrderId, Points > 0).
            var earned = await _db.PointsLedgerEntries
                .Where(p => p.OrderId == orderId && p.Points > 0)
                .SumAsync(p => (int?)p.Points) ?? 0;
            // Points the buyer redeemed (tracked on the order; the redeem ledger row has OrderId = null).
            var redeemed = order.LoyaltyRedeemedAt != null ? order.LoyaltyPointsRedeemed : 0;

            if (earned > 0)
                account.Entries.Add(new PointsLedgerEntry
                {
                    Points = -earned, Reason = $"Reversed on refund of order {order.OrderNumber}",
                    OrderId = null, CreatedAt = now
                });
            if (redeemed > 0)
                account.Entries.Add(new PointsLedgerEntry
                {
                    Points = redeemed, Reason = $"Returned on refund of order {order.OrderNumber}",
                    OrderId = null, CreatedAt = now
                });

            var newBalance = account.PointsBalance + redeemed - earned;
            if (newBalance < 0)
            {
                _logger.LogWarning("Loyalty reversal for {OrderNumber} would go negative ({Balance}); clamped to 0.",
                    order.OrderNumber, newBalance);
                newBalance = 0;
            }
            account.PointsBalance = newBalance;
            account.UpdatedAt = now;
        }

        await _db.SaveChangesAsync();
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
