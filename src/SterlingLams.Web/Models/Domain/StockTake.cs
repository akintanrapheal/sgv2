namespace SterlingLams.Web.Models.Domain;

/// <summary>A completed stock-take session for one branch — the audit record behind the EPOS-style
/// "Stock Takes" history. Each counted line is reconciled to on-hand through the StockMovement ledger
/// (reason "Stock-take"); this header + its lines preserve the expected/actual/variance snapshot.</summary>
public class StockTake
{
    public int Id { get; set; }

    /// <summary>Human reference, e.g. "ST00042".</summary>
    public string Reference { get; set; } = string.Empty;

    public int StoreId { get; set; }
    public Store Store { get; set; } = null!;

    public string? StaffUserId { get; set; }
    /// <summary>Name snapshot of the staff member who ran the count.</summary>
    public string StaffName { get; set; } = string.Empty;

    public string Status { get; set; } = "Completed";
    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<StockTakeLine> Lines { get; set; } = new List<StockTakeLine>();

    // Convenience roll-ups for the history list.
    public int ItemCount => Lines.Count;
    public int Discrepancies => Lines.Count(l => l.CountedQty != l.ExpectedQty);
}

public class StockTakeLine
{
    public int Id { get; set; }

    public int StockTakeId { get; set; }
    public StockTake StockTake { get; set; } = null!;

    public int ProductId { get; set; }
    public int? ProductVariantId { get; set; }

    // Snapshots so the record reads correctly even after a rename.
    public string ProductName { get; set; } = string.Empty;
    public string? Barcode { get; set; }
    public string CategoryName { get; set; } = string.Empty;

    /// <summary>System on-hand at the time of the count.</summary>
    public int ExpectedQty { get; set; }
    /// <summary>Physical count entered by the counter.</summary>
    public int CountedQty { get; set; }

    public int Variance => CountedQty - ExpectedQty;

    /// <summary>Per-line reason for a difference (Missing Stock, New Stock, Internal Movement, …).</summary>
    public string? Reason { get; set; }
}
