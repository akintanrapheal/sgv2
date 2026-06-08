namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// Moves stock from one branch to another. Deducts the source and adds the destination,
/// both recorded in the stock ledger (Type Transfer).
/// </summary>
public class StockTransfer
{
    public int Id { get; set; }
    public string TransferNumber { get; set; } = string.Empty;

    public int FromStoreId { get; set; }
    public Store FromStore { get; set; } = null!;

    public int ToStoreId { get; set; }
    public Store ToStore { get; set; } = null!;

    public string? CreatedByUserId { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

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

    public int Quantity { get; set; }
}
