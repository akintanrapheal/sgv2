namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// A customer's loyalty points wallet. One per user. <see cref="PointsBalance"/> is a running total
/// kept in step with the append-only <see cref="PointsLedgerEntry"/> rows for fast reads.
/// </summary>
public class LoyaltyAccount
{
    public int Id { get; set; }

    public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;

    public int PointsBalance { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<PointsLedgerEntry> Entries { get; set; } = new List<PointsLedgerEntry>();
}

/// <summary>
/// One movement on a loyalty wallet (append-only). Positive = earned, negative = redeemed.
/// Accruals carry the source <see cref="OrderId"/>; a unique index on it makes accrual idempotent
/// (an order can only ever award points once).
/// </summary>
public class PointsLedgerEntry
{
    public int Id { get; set; }

    public int LoyaltyAccountId { get; set; }
    public LoyaltyAccount Account { get; set; } = null!;

    public int Points { get; set; }
    public string Reason { get; set; } = string.Empty;

    /// <summary>The order that earned these points (null for non-order adjustments).</summary>
    public int? OrderId { get; set; }
    public Order? Order { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
