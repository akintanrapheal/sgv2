namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// Single source of truth for the adjustment reasons offered in the UI and how each maps to a
/// ledger movement type (so receipts, damage and shrinkage stay first-class and reportable).
/// </summary>
public static class AdjustmentReasons
{
    public static readonly string[] All = { "Stock count", "Received", "Damage", "Loss / theft", "Correction" };

    public static StockMovementType MovementType(string reason) => reason switch
    {
        "Received"     => StockMovementType.Purchase,
        "Damage"       => StockMovementType.Damage,
        "Loss / theft" => StockMovementType.Loss,
        _              => StockMovementType.Adjustment,
    };
}

/// <summary>
/// Header for a multi-line stock adjustment (Moniebook-style "BSA#####" record). Groups the
/// individual ledger movements made in one save/form submission under a single reference, branch
/// and reason, so adjustments are auditable as a unit (not just loose StockMovement rows).
/// Each line still raises a StockMovement; the movement's Reference carries the AdjustmentNumber.
/// </summary>
public class StockAdjustment
{
    public int Id { get; set; }

    /// <summary>Human reference, e.g. "BSA00085".</summary>
    public string AdjustmentNumber { get; set; } = string.Empty;

    public int StoreId { get; set; }
    public Store Store { get; set; } = null!;

    /// <summary>Reason label (Stock count / Received / Damage / Loss-theft / Correction…).</summary>
    public string Reason { get; set; } = string.Empty;

    public string? Note { get; set; }

    /// <summary>How the record was created — "Grid" (inline stock page) or "Form" (dedicated form).</summary>
    public string Source { get; set; } = "Form";

    public string? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<StockAdjustmentLine> Lines { get; set; } = new List<StockAdjustmentLine>();
}

public class StockAdjustmentLine
{
    public int Id { get; set; }

    public int StockAdjustmentId { get; set; }
    public StockAdjustment StockAdjustment { get; set; } = null!;

    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public int? ProductVariantId { get; set; }
    public ProductVariant? ProductVariant { get; set; }

    // Snapshots so the record reads correctly even if the product/variant is later renamed.
    public string ProductName { get; set; } = string.Empty;
    public string? VariantName { get; set; }

    /// <summary>Signed change applied to on-hand: positive = added, negative = removed.</summary>
    public int QtyDelta { get; set; }

    /// <summary>On-hand balance for this row after the line was applied.</summary>
    public int BalanceAfter { get; set; }

    /// <summary>Optional unit cost captured on received goods.</summary>
    public decimal? UnitCost { get; set; }

    /// <summary>Optional expiry date for perishable/dated stock.</summary>
    public DateTime? ExpiryDate { get; set; }
}
