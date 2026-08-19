namespace SterlingLams.Web.Models.Domain;

/// <summary>Finance approval state of a refund. A refund is REQUESTED first and does not move any
/// money or stock until Finance approves it (or rejects it).</summary>
public enum RefundStatus
{
    PendingApproval = 0,
    Approved = 1,
    Rejected = 2,
}

/// <summary>Inventory's per-item decision on a returned unit once Finance has approved the refund:
/// put it back on the shelf, or write it off as damaged (recorded as shrinkage). Only meaningful when
/// the refund requested a restock.</summary>
public enum RestockDecision
{
    Pending = 0,
    Restocked = 1,
    WrittenOff = 2,
}

/// <summary>
/// A return/refund against a sale (POS or online). It is created as a REQUEST (PendingApproval) —
/// nothing is paid out or restocked until Finance approves it, at which point the payout, stock
/// return, loyalty/gift-card reversal and gateway refund are applied. Kept separate from Orders so
/// sales totals and refunds report cleanly.
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

    // ── Finance approval ────────────────────────────────────────────────────────
    /// <summary>Only an Approved refund has actually paid out money / moved stock. Reports count
    /// approved refunds only; pending/rejected must not inflate figures.</summary>
    public RefundStatus Status { get; set; } = RefundStatus.PendingApproval;
    public string? ApprovedByUserId { get; set; }
    public DateTime? DecisionAt { get; set; }
    public string? DecisionNote { get; set; }

    /// <summary>Whether the requester intends the returned items to go back to sellable stock. The
    /// actual stock return happens on approval (and, later, via the Inventory restock queue).</summary>
    public bool RestockRequested { get; set; }
    /// <summary>Store the units return to when restocked (order's fulfilling/pickup branch, or the till's).</summary>
    public int? RestockStoreId { get; set; }
    /// <summary>Captured at request time — whether this refund fully covers the order (drives loyalty /
    /// gift-card reversal and the order's Refunded status when approved).</summary>
    public bool WasFullRefund { get; set; }

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

    // ── Inventory restock decision (Step 2) ──────────────────────────────────────
    /// <summary>Set by Inventory after Finance approves a restock-requested refund: Restocked puts the
    /// units back; WrittenOff records a damaged-return write-off (shrinkage) and keeps them off the shelf.</summary>
    public RestockDecision RestockDecision { get; set; } = RestockDecision.Pending;
    public string? RestockDecidedByUserId { get; set; }
    public DateTime? RestockDecidedAt { get; set; }

    /// <summary>How many of the returned units were put back on the shelf (the rest were written off).</summary>
    public int RestockedQuantity { get; set; }
    /// <summary>Why the not-restocked units were kept off the shelf (Damaged / Wrong item sold / …).</summary>
    public string? RestockNote { get; set; }
}
