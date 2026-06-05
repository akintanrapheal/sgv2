using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services.Inventory;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Background service that periodically syncs inventory from ERPNext
/// into the local StoreInventory table.
/// </summary>
public class InventorySyncHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InventorySyncHostedService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(1);

    public InventorySyncHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<InventorySyncHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Inventory sync service started. Interval: {Interval}", _interval);

        // Initial delay to let the app fully start
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await SyncInventoryAsync(stoppingToken);
            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task SyncInventoryAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();

            var itemCodes = await db.Products
                .Where(p => p.IsActive && !string.IsNullOrEmpty(p.ErpNextItemCode))
                .Select(p => p.ErpNextItemCode)
                .ToArrayAsync(ct);

            if (itemCodes.Length == 0)
            {
                _logger.LogDebug("No active products to sync.");
                return;
            }

            _logger.LogInformation("Syncing inventory for {Count} products from ERPNext...", itemCodes.Length);

            // Sync in batches of 50 to avoid large ERPNext requests
            var batches = itemCodes.Chunk(50);
            foreach (var batch in batches)
            {
                if (ct.IsCancellationRequested) break;
                await inventory.SyncProductInventoryAsync(batch);
            }

            _logger.LogInformation("Inventory sync complete.");
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Inventory sync failed. Will retry in {Interval}.", _interval);
        }
    }
}
