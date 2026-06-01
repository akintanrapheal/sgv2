using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services.Odoo;

namespace SterlingLams.Web.Services.Inventory;

public interface IInventoryService
{
    Task<Dictionary<int, int>> GetStoreInventoryForProductAsync(int odooProductId);
    Task SyncProductInventoryAsync(int[] odooProductIds);
    Task SyncAllAsync();
    Task<bool> IsAvailableInStoreAsync(int odooProductId, int storeId, int requiredQty = 1);
}

public class InventoryService : IInventoryService
{
    private readonly IOdooService _odoo;
    private readonly ApplicationDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly OdooSettings _odooSettings;
    private readonly ILogger<InventoryService> _logger;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    public InventoryService(
        IOdooService odoo,
        ApplicationDbContext db,
        IMemoryCache cache,
        OdooSettings odooSettings,
        ILogger<InventoryService> logger)
    {
        _odoo = odoo;
        _db = db;
        _cache = cache;
        _odooSettings = odooSettings;
        _logger = logger;
    }

    /// <summary>Returns storeId → availableQty for a given Odoo product.</summary>
    public async Task<Dictionary<int, int>> GetStoreInventoryForProductAsync(int odooProductId)
    {
        var cacheKey = $"inventory:product:{odooProductId}";

        if (_cache.TryGetValue(cacheKey, out Dictionary<int, int>? cached) && cached != null)
            return cached;

        var inventoryMap = await _odoo.GetInventoryByStoreAsync(new[] { odooProductId });
        var result = inventoryMap.TryGetValue(odooProductId, out var storeMap) ? storeMap : new Dictionary<int, int>();

        _cache.Set(cacheKey, result, TimeSpan.FromSeconds(_odooSettings.InventoryCacheTtlSeconds));
        return result;
    }

    /// <summary>Syncs stock from Odoo into the local DB for given product IDs.</summary>
    public async Task SyncProductInventoryAsync(int[] odooProductIds)
    {
        try
        {
            var inventoryMap = await _odoo.GetInventoryByStoreAsync(odooProductIds);

            var stores = await _db.Stores.ToListAsync();
            var products = await _db.Products
                .Where(p => odooProductIds.Contains(p.OdooProductId))
                .ToListAsync();

            foreach (var (odooProductId, storeStockMap) in inventoryMap)
            {
                var product = products.FirstOrDefault(p => p.OdooProductId == odooProductId);
                if (product == null) continue;

                foreach (var (odooWarehouseId, qty) in storeStockMap)
                {
                    var store = stores.FirstOrDefault(s => s.OdooWarehouseId == odooWarehouseId);
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
            _logger.LogInformation("Synced inventory for {Count} products", odooProductIds.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync inventory for products {Ids}", string.Join(",", odooProductIds));
            throw;
        }
    }

    public async Task<bool> IsAvailableInStoreAsync(int odooProductId, int storeId, int requiredQty = 1)
    {
        var inventory = await GetStoreInventoryForProductAsync(odooProductId);
        return inventory.TryGetValue(storeId, out var qty) && qty >= requiredQty;
    }

    /// <summary>Syncs all active products' inventory from Odoo in batches.</summary>
    public async Task SyncAllAsync()
    {
        var odooProductIds = await _db.Products
            .Where(p => p.IsActive)
            .Select(p => p.OdooProductId)
            .ToArrayAsync();

        if (odooProductIds.Length == 0) return;

        foreach (var batch in odooProductIds.Chunk(50))
            await SyncProductInventoryAsync(batch);
    }
}
