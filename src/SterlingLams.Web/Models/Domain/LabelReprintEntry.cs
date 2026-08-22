namespace SterlingLams.Web.Models.Domain;

public enum ReprintStatus
{
    Pending = 0,
    Printed = 1,
    Dismissed = 2
}

/// <summary>
/// A product whose printed price tag is now out of date because its price changed on the backend.
/// One Pending row per product (deduped); it leaves the queue when its label is printed from the
/// queue or a staff member dismisses it. Only products that have stock on hand are ever queued —
/// there has to be a physical tag to replace.
/// </summary>
public class LabelReprintEntry
{
    public int Id { get; set; }

    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public ReprintStatus Status { get; set; } = ReprintStatus.Pending;

    /// <summary>Short human note on why a reprint is needed, e.g. "Price ₦12,000 → ₦13,500".</summary>
    public string Reason { get; set; } = "Price changed";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
}
