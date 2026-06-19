namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// Inter-branch transfer workflow: Requested → Approved → In Transit → Received
/// (Completed or Partially Received), with Rejected/Cancelled side-branches.
/// Stock moves (deduct source / add destination) are recorded in the stock ledger
/// (Type Transfer) at Dispatch and Receive respectively.
/// </summary>
public enum TransferStatus
{
    Draft = 0,
    PendingApproval = 1,
    Approved = 2,
    InTransit = 3,
    PartiallyReceived = 4,
    Completed = 5,
    Rejected = 6,
    Cancelled = 7
}

public class StockTransfer
{
    public int Id { get; set; }
    public string TransferNumber { get; set; } = string.Empty;

    public int FromStoreId { get; set; }
    public Store FromStore { get; set; } = null!;

    public int ToStoreId { get; set; }
    public Store ToStore { get; set; } = null!;

    /// <summary>Set when this transfer was auto-created to consolidate stock for an online order;
    /// once all of an order's transfers are received the sale is committed and the order ships.</summary>
    public int? OrderId { get; set; }

    public TransferStatus Status { get; set; } = TransferStatus.PendingApproval;

    public string? CreatedByUserId { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string? ApprovedByUserId { get; set; }
    public DateTime? ApprovedAt { get; set; }

    public string? RejectedByUserId { get; set; }
    public DateTime? RejectedAt { get; set; }
    public string? RejectionReason { get; set; }

    public string? DispatchedByUserId { get; set; }
    public DateTime? DispatchedAt { get; set; }
    public string? TrackingNumber { get; set; }
    public string? CourierName { get; set; }
    public string? DispatchNotes { get; set; }

    public string? ReceivedByUserId { get; set; }
    public DateTime? ReceivedAt { get; set; }
    public string? ReceiveNotes { get; set; }

    public string? CancelledByUserId { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancellationReason { get; set; }

    public ICollection<StockTransferItem> Items { get; set; } = new List<StockTransferItem>();
}

public class StockTransferItem
{
    public int Id { get; set; }

    public int StockTransferId { get; set; }
    public StockTransfer StockTransfer { get; set; } = null!;

    public int ProductId { get; set; }
    public int? ProductVariantId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string? VariantName { get; set; }

    public int RequestedQty { get; set; }
    public int? ApprovedQty { get; set; }
    public int? DispatchedQty { get; set; }

    // Receive reconciliation (Moniebook-style): of the dispatched units, how many arrived good,
    // arrived damaged, or were written off as won't-fulfil. Pending is whatever is still expected.
    public int? ReceivedQty { get; set; }
    public int? DamagedQty { get; set; }
    public int? WontFulfilQty { get; set; }

    /// <summary>Units still in transit / unaccounted: dispatched − received − damaged − won't-fulfil.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public int PendingQty => Math.Max(0, (DispatchedQty ?? 0) - (ReceivedQty ?? 0) - (DamagedQty ?? 0) - (WontFulfilQty ?? 0));
}
