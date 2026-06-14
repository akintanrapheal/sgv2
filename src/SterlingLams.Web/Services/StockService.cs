using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

/// <summary>Thrown by <see cref="IStockService.ApplyAsync"/> when a stock change would
/// drive QuantityOnHand negative. Callers should catch this, roll back the transaction,
/// and surface a friendly "not enough stock" message.</summary>
public class InsufficientStockException : Exception
{
    public int ProductId { get; }
    public int StoreId { get; }
    public int Available { get; }
    public int Requested { get; }

    public InsufficientStockException(int productId, int storeId, int available, int quantityChange)
        : base($"Insufficient stock for product {productId} at store {storeId}: have {available}, requested {-quantityChange}.")
    {
        ProductId = productId;
        StoreId = storeId;
        Available = available;
        Requested = -quantityChange;
    }
}

public interface IStockService
{
    /// <summary>Current on-hand quantity for a product at a store (0 if no record).</summary>
    Task<int> GetStockAsync(int productId, int storeId);

    /// <summary>
    /// Applies a stock change: updates the running balance (StoreInventory) and appends a
    /// ledger entry (StockMovement). Does NOT call SaveChanges — the caller owns the transaction
    /// so multiple lines (and the related order) commit together. Returns the new balance.
    /// </summary>
    Task<int> ApplyAsync(int productId, int? variantId, int storeId, int quantityChange,
        StockMovementType type, string? reference = null, string? note = null, string? userId = null);
}

public class StockService : IStockService
{
    private readonly ApplicationDbContext _db;

    public StockService(ApplicationDbContext db) => _db = db;

    public async Task<int> GetStockAsync(int productId, int storeId)
    {
        // AsNoTracking: callers often check stock both before and after acquiring a row lock
        // (e.g. TillController.Checkout). A tracking read here would seed the change tracker
        // with pre-lock values, so EF's identity map would hand the same stale instance (and
        // stale xmin) back to the later ApplyAsync, causing a spurious
        // DbUpdateConcurrencyException on save even when the stock change is valid.
        var inv = await _db.StoreInventories.AsNoTracking()
            .FirstOrDefaultAsync(si => si.ProductId == productId && si.StoreId == storeId);
        return inv?.QuantityOnHand ?? 0;
    }

    public async Task<int> ApplyAsync(int productId, int? variantId, int storeId, int quantityChange,
        StockMovementType type, string? reference = null, string? note = null, string? userId = null)
    {
        var now = DateTime.UtcNow;

        var inv = await _db.StoreInventories
            .FirstOrDefaultAsync(si => si.ProductId == productId && si.StoreId == storeId);
        if (inv == null)
        {
            inv = new StoreInventory { ProductId = productId, StoreId = storeId, QuantityOnHand = 0, LastSyncedAt = now };
            _db.StoreInventories.Add(inv);
        }

        var newQty = inv.QuantityOnHand + quantityChange;
        if (newQty < 0)
            throw new InsufficientStockException(productId, storeId, inv.QuantityOnHand, quantityChange);

        inv.QuantityOnHand = newQty;
        inv.LastSyncedAt = now;

        _db.StockMovements.Add(new StockMovement
        {
            ProductId = productId,
            ProductVariantId = variantId,
            StoreId = storeId,
            QuantityChange = quantityChange,
            BalanceAfter = inv.QuantityOnHand,
            Type = type,
            Reference = reference,
            Note = note,
            CreatedByUserId = userId,
            CreatedAt = now
        });

        return inv.QuantityOnHand;
    }
}
