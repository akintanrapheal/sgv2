namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// A return/refund against a POS sale. Puts stock back (via the ledger) and records the money
/// returned. Kept separate from Orders so sales totals and refunds report cleanly.
/// </summary>
public class Refund
{
    public int Id { get; set; }
    public string RefundNumber { get; set; } = string.Empty;

    public int OriginalOrderId { get; set; }
    public Order OriginalOrder { get; set; } = null!;

    public int? RegisterId { get; set; }
    public int? TillSessionId { get; set; }
    public string CashierUserId { get; set; } = string.Empty;

    public string RefundMethod { get; set; } = "Cash"; // Cash / Card / Transfer
    public decimal Amount { get; set; }
    public string? Reason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<RefundItem> Items { get; set; } = new List<RefundItem>();
}

public class RefundItem
{
    public int Id { get; set; }

    public int RefundId { get; set; }
    public Refund Refund { get; set; } = null!;

    public int ProductId { get; set; }
    public int? ProductVariantId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string? VariantName { get; set; }

    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal => Quantity * UnitPrice;
}
