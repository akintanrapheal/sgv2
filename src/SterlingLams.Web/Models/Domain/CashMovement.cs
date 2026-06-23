namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// A cash drop or top-up recorded against an open till session during a shift — e.g. paying a
/// supplier from the drawer (pay-out), or adding change/float (pay-in). Folds into the Z-report's
/// expected-cash so cash-up still balances. <see cref="Amount"/> is signed: positive = cash in,
/// negative = cash out.
/// </summary>
public class CashMovement
{
    public int Id { get; set; }

    public int TillSessionId { get; set; }
    public TillSession TillSession { get; set; } = null!;

    public int RegisterId { get; set; }

    /// <summary>The cashier who recorded the movement.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Signed amount: positive = cash in (pay-in / float top-up), negative = cash out (pay-out).</summary>
    public decimal Amount { get; set; }

    public string? Reason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
