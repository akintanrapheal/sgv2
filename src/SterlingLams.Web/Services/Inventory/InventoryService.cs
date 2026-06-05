using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services.ERPNext;

namespace SterlingLams.Web.Services.Inventory;

public interface IInventoryService
{
    /// <summary>Returns localStoreId → availableQty for a given ERPNext item code.</summary>
    Task<Dictionary<int, int>> GetStoreInventoryForProductAsync(string itemCode);
    Task SyncProductInventoryAsync(string[] itemCodes);
    Task SyncAllAsync();
    Task<bool> IsAvailableInStoreAsync(string itemCode, int storeId, int requiredQty = 1);
}

public class InventoryService : IInventoryService
{
    private readonly IERPNextService _erpNext;
    private readonly ApplicationDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ERPNextSettings _settings;
    private readonly ILogger<InventoryService> _logger;

    public InventoryService(
        IERPNextService erpNext,
        ApplicationDbContext db,
        IMemoryCache cache,
        ERPNextSettings settings,
        ILogger<InventoryService> logger)
    {
        _erpNext = erpNext;
        _db = db;
        _cache = cache;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Returns localStoreId → availableQty, resolved via ERPNext Bin and local store mapping.</summary>
    public async Task<Dictionary<int, int>> GetStoreInventoryForProductAsync(string itemCode)
    {
        var cacheKey = $"inventory:product:{itemCode}";

        if (_cache.TryGetValue(cacheKey, out Dictionary<int, int>? cached) && cached != null)
            return cached;

        var stores = await _db.Stores.ToListAsync();
        var inventoryMap = await _erpNext.GetInventoryByWarehouseAsync(new[] { itemCode });
        var warehouseQty = inventoryMap.TryGetValue(itemCode, out var wh)
            ? wh
            : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<int, int>();
        foreach (var store in stores)
        {
            if (!string.IsNullOrEmpty(store.ErpNextWarehouse) &&
                warehouseQty.TryGetValue(store.ErpNextWarehouse, out var qty))
            {
                result[store.Id] = qty;
            }
        }

        _cache.Set(cacheKey, result, TimeSpan.FromSeconds(_settings.InventoryCacheTtlSeconds));
        return result;
    }

    /// <summary>Syncs stock from ERPNext Bin into the local StoreInventory table.</summary>
    public async Task SyncProductInventoryAsync(string[] itemCodes)
    {
        try
        {
            var inventoryMap = await _erpNext.GetInventoryByWarehouseAsync(itemCodes);

            var stores = await _db.Stores.ToListAsync();
            var products = await _db.Products
                .Where(p => itemCodes.Contains(p.ErpNextItemCode))
                .ToListAsync();

            foreach (var (itemCode, warehouseQty) in inventoryMap)
            {
                var product = products.FirstOrDefault(p => p.ErpNextItemCode == itemCode);
                if (product == null) continue;

                foreach (var (warehouse, qty) in warehouseQty)
                {
                    var store = stores.FirstOrDefault(s =>
                        s.ErpNextWarehouse.Equals(warehouse, StringComparison.OrdinalIgnoreCase));
                    if (store == null) continue;

                    var existing = await _db.StoreInventories
                        .FirstOrDefaultAsync(si => si.ProductId == product.Id && si.StoreId == store.Id);

                    if (existing != null)
                    {
                        existing.QuantityOnHand = qty;
                        existing.LastSyncedAt = DateTime.UtcNow;
                    }
                    else
                    {
                        _db.StoreInventories.Add(new StoreInventory
                        {
                            ProductId = product.Id,
                            StoreId = store.Id,
                            QuantityOnHand = qty,
                            LastSyncedAt = DateTime.UtcNow
                        });
                    }
                }
            }

            await _db.SaveChangesAsync();
            _logger.LogInformation("Synced inventory for {Count} items", itemCodes.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync inventory for items {Codes}", string.Join(",", itemCodes));
            throw;
        }
    }

    public async Task<bool> IsAvailableInStoreAsync(string itemCode, int storeId, int requiredQty = 1)
    {
        var inventory = await GetStoreInventoryForProductAsync(itemCode);
        return inventory.TryGetValue(storeId, out var qty) && qty >= requiredQty;
    }

    /// <summary>Syncs all active products' inventory from ERPNext in batches of 50.</summary>
    public async Task SyncAllAsync()
    {
        var itemCodes = await _db.Products
            .Where(p => p.IsActive && !string.IsNullOrEmpty(p.ErpNextItemCode))
            .Select(p => p.ErpNextItemCode)
            .ToArrayAsync();

        if (itemCodes.Length == 0) return;

        foreach (var batch in itemCodes.Chunk(50))
            await SyncProductInventoryAsync(batch);
    }
}
