namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// A like-for-like REPLACEMENT against a paid online order: an item the customer received can't be
/// kept (tarnished / faulty / wrong), so a replacement item is sent out and the bad one comes back.
///
/// Unlike a refund, no money is paid out — the replacement item's stock is deducted when the
/// replacement is raised, and the returned (bad) item then goes to the Inventory returns queue where
/// it is put back on the shelf or written off as damaged (<see cref="RestockDecision"/>, the same
/// Step-2 decision a refund's returned items get). If the replacement costs more, the extra balance
/// the customer paid is recorded here manually (document-only).
/// </summary>
public class OrderReplacement
{
    public int Id { get; set; }
    public string ReplacementNumber { get; set; } = string.Empty;

    public int OriginalOrderId { get; set; }
    public Order OriginalOrder { get; set; } = null!;

    /// <summary>Branch the replacement stock is deducted from and the returned item comes back to
    /// (the order's fulfilling store, else its pickup store).</summary>
    public int StoreId { get; set; }

    /// <summary>Why the original item can't be kept (tarnished / faulty / wrong item …).</summary>
    public string? Reason { get; set; }

    // ── The bad item coming back (one order line) ────────────────────────────────
    public int OldProductId { get; set; }
    public int? OldProductVariantId { get; set; }
    public string OldProductName { get; set; } = string.Empty;
    public string? OldVariantName { get; set; }
    public int OldQuantity { get; set; }

    // ── The replacement item going out (stock deducted at StoreId when raised) ────
    public int NewProductId { get; set; }
    public int? NewProductVariantId { get; set; }
    public string NewProductName { get; set; } = string.Empty;
    public string? NewVariantName { get; set; }
    public int NewQuantity { get; set; }

    /// <summary>Extra the customer paid when the replacement costs more — recorded manually
    /// (document-only; it does NOT change the order total or post to the gateway).</summary>
    public decimal BalancePaid { get; set; }
    /// <summary>How the balance was collected (transfer ref, POS receipt no., …).</summary>
    public string? BalanceNote { get; set; }

    // ── Inventory disposition of the returned (bad) item — same as a refund's Step 2 ─────────────
    public RestockDecision RestockDecision { get; set; } = RestockDecision.Pending;
    public int RestockedQuantity { get; set; }
    public string? RestockNote { get; set; }
    public string? RestockDecidedByUserId { get; set; }
    public DateTime? RestockDecidedAt { get; set; }

    public string? CreatedByUserId { get; set; }
    public string? CreatedByName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
