namespace SterlingLams.Web.Models.Domain;

public enum ReprintStatus
{
    Pending = 0,
    Printed = 1,
    Dismissed = 2
}

/// <summary>
/// A product whose printed price tag is now out of date because its price changed on the backend.
/// Tracked <b>per branch</b>: one Pending row per (product, store) that holds stock, because each
/// branch has its own printed tag and reprints (and clears) independently. It leaves the queue when
/// its label is printed from the queue or a staff member dismisses it. Only branches with stock on
/// hand are queued — there has to be a physical tag to replace.
/// </summary>
public class LabelReprintEntry
{
    public int Id { get; set; }

    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;

    /// <summary>The branch whose tag needs reprinting (it had stock of this product when the price changed).</summary>
    public int StoreId { get; set; }
    public Store Store { get; set; } = null!;

    public ReprintStatus Status { get; set; } = ReprintStatus.Pending;

    /// <summary>Short human note on why a reprint is needed, e.g. "Price ₦12,000 → ₦13,500".</summary>
    public string Reason { get; set; } = "Price changed";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
}
