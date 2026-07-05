namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// A snapshot of a shopper's cart taken when they reached checkout, used to email a recovery link if
/// they don't complete payment. One row per email (refreshed on each checkout attempt). Survives the
/// 30-min auto-cancel of unpaid orders, so the hours-later recovery email still has the items.
/// </summary>
public class AbandonedCart
{
    public int Id { get; set; }

    public string Email { get; set; } = string.Empty;
    /// <summary>Opaque token for the recovery link (regenerated each checkout).</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>JSON snapshot of the cart items (productId / variantId / quantity).</summary>
    public string ItemsJson { get; set; } = "[]";
    public decimal Subtotal { get; set; }
    public int ItemCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Time the most recent reminder was sent (the recovery sequence sends several).</summary>
    public DateTime? EmailedAt { get; set; }
    /// <summary>How many reminders in the recovery sequence have gone out (0..N).</summary>
    public int RemindersSent { get; set; }
    /// <summary>Set when the shopper completed a paid order (or used the recovery link) — no email.</summary>
    public DateTime? RecoveredAt { get; set; }
}
