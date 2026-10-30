namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// One snapshot row written just before the "Cap discounts at 15%" tool raises a discounted price.
/// Stores the ORIGINAL value so the whole run can be reverted (restores the deeper discounts).
/// One <see cref="BatchId"/> groups a single Apply run; Revert restores the latest un-reverted batch.
/// </summary>
public class PriceCapBackup
{
    public int Id { get; set; }

    /// <summary>Groups all rows written by one Apply run.</summary>
    public Guid BatchId { get; set; }

    public int ProductId { get; set; }

    /// <summary>Null = the field is on the product itself; otherwise the variant it belongs to.</summary>
    public int? ProductVariantId { get; set; }

    /// <summary>"SalePrice" or "PosPrice" — which field was capped.</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>The value before the cap (what Revert restores). Null is possible in theory but the
    /// tool only backs up fields that had a value below the cap.</summary>
    public decimal? OldValue { get; set; }

    /// <summary>The value the tool wrote (for the record / audit; Revert uses <see cref="OldValue"/>).</summary>
    public decimal NewValue { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Set true once this batch has been reverted, so it isn't restored twice.</summary>
    public bool Reverted { get; set; }
    public DateTime? RevertedAt { get; set; }
}
