using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Inventory;

/// <summary>Shared label/badge/filter mappings for TransferStatus, used by the Transfers Index and Detail views.</summary>
public static class TransferStatusDisplay
{
    public static string Label(TransferStatus status) => status switch
    {
        TransferStatus.Draft => "Draft",
        TransferStatus.PendingApproval => "Pending Approval",
        TransferStatus.Approved => "Approved",
        TransferStatus.InTransit => "In Transit",
        TransferStatus.PartiallyReceived => "Partially Received",
        TransferStatus.Completed => "Completed",
        TransferStatus.Rejected => "Rejected",
        TransferStatus.Cancelled => "Cancelled",
        _ => status.ToString()
    };

    public static string BadgeClass(TransferStatus status) => status switch
    {
        TransferStatus.Draft => "bg-neutral-100 text-neutral-500",
        TransferStatus.PendingApproval => "bg-amber-50 text-amber-700",
        TransferStatus.Approved => "bg-blue-50 text-blue-700",
        TransferStatus.InTransit => "bg-indigo-50 text-indigo-700",
        TransferStatus.PartiallyReceived => "bg-orange-50 text-orange-700",
        TransferStatus.Completed => "bg-emerald-50 text-emerald-700",
        TransferStatus.Rejected => "bg-red-50 text-red-700",
        TransferStatus.Cancelled => "bg-neutral-100 text-neutral-400",
        _ => "bg-neutral-100 text-neutral-500"
    };

    /// <summary>Statuses with their Index status-filter query value, in display order.</summary>
    public static readonly (TransferStatus Status, string Filter)[] FilterOptions =
    {
        (TransferStatus.PendingApproval, "pending"),
        (TransferStatus.Approved, "approved"),
        (TransferStatus.InTransit, "intransit"),
        (TransferStatus.PartiallyReceived, "partial"),
        (TransferStatus.Completed, "completed"),
        (TransferStatus.Rejected, "rejected"),
        (TransferStatus.Cancelled, "cancelled"),
    };
}
