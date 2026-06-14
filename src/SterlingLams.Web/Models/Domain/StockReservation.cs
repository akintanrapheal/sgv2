namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// A soft hold on stock for an unpaid online order. Created at checkout (bumps
/// <see cref="StoreInventory.QuantityReserved"/>) so concurrent orders can't oversell the same
/// units before payment. Released when the order is paid (converted to a sale) or abandoned
/// (freed by the sweeper). One row per (order, store, product) allocation.
/// </summary>
public class StockReservation
{
    public int Id { get; set; }

    public int OrderId { get; set; }
    public Order Order { get; set; } = null!;

    public int StoreId { get; set; }
    public int ProductId { get; set; }
    /// <summary>Variant the hold is for (NULL = product-level pool, mirroring StoreInventory).</summary>
    public int? ProductVariantId { get; set; }
    public int Quantity { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
