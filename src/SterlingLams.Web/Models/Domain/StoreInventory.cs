namespace SterlingLams.Web.Models.Domain;

/// <summary>On-hand stock per product per store, maintained through the StockMovement ledger
/// (POS sales, transfers, adjustments, online fulfilment).</summary>
public class StoreInventory
{
    public int Id { get; set; }

    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public int StoreId { get; set; }
    public Store Store { get; set; } = null!;

    public int QuantityOnHand { get; set; }
    public int QuantityReserved { get; set; }

    public int AvailableQuantity => Math.Max(0, QuantityOnHand - QuantityReserved);

    public DateTime LastSyncedAt { get; set; } = DateTime.UtcNow;
}
