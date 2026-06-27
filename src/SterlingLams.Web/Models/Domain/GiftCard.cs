using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// A prepaid gift card: a unique code carrying a ₦ balance, redeemable at checkout.
/// Issued from Admin; partial redemption is supported (the balance is drawn down per order).
/// </summary>
public class GiftCard
{
    public int Id { get; set; }

    /// <summary>Human-friendly, unique redemption code (e.g. "SLGC-7F3K-9Q8M"). Stored uppercase.</summary>
    [MaxLength(40)]
    public string Code { get; set; } = string.Empty;

    /// <summary>Face value the card was issued with (₦). Never changes.</summary>
    public decimal InitialAmount { get; set; }

    /// <summary>Remaining spendable balance (₦). Drawn down as orders redeem it.</summary>
    public decimal Balance { get; set; }

    /// <summary>An admin can deactivate a card (lost/fraud) without deleting its history.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Optional expiry; null = never expires.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Who the card was issued for (for the admin's records / optional emailing). Optional.</summary>
    [MaxLength(200)]
    public string? RecipientName { get; set; }
    [MaxLength(256)]
    public string? RecipientEmail { get; set; }

    /// <summary>Free-text note (occasion, source, etc.). Optional.</summary>
    [MaxLength(500)]
    public string? Note { get; set; }

    /// <summary>Staff user who issued the card. Optional.</summary>
    public string? IssuedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }

    public ICollection<GiftCardTransaction> Transactions { get; set; } = new List<GiftCardTransaction>();

    /// <summary>True when the card can currently fund an order (active, in-date, with balance).</summary>
    [NotMapped]
    public bool IsRedeemable =>
        IsActive && Balance > 0 && (ExpiresAt == null || ExpiresAt > DateTime.UtcNow);

    [NotMapped]
    public bool IsExpired => ExpiresAt != null && ExpiresAt <= DateTime.UtcNow;
}

public enum GiftCardTxnType
{
    Issue,      // card created with its initial balance (+)
    Redeem,     // balance drawn down by an order (−)
    Refund,     // balance returned to the card on an order refund (+)
    Adjust      // manual admin top-up / correction (±)
}

/// <summary>An immutable ledger entry recording every balance change on a gift card.</summary>
public class GiftCardTransaction
{
    public int Id { get; set; }

    public int GiftCardId { get; set; }
    public GiftCard? GiftCard { get; set; }

    /// <summary>Signed ₦ change: positive credits the card, negative draws it down.</summary>
    public decimal Amount { get; set; }

    public GiftCardTxnType Type { get; set; }

    /// <summary>The order this entry relates to (for Redeem/Refund), if any.</summary>
    public int? OrderId { get; set; }

    [MaxLength(300)]
    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
