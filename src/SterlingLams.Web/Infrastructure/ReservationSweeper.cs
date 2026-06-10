using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Periodically frees stock reserved by online orders that were placed but never paid (the
/// customer abandoned checkout), and cancels those orders — so held stock returns to sale.
/// </summary>
public class ReservationSweeper : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30); // unpaid hold lifetime

    private readonly IServiceProvider _sp;
    private readonly ILogger<ReservationSweeper> _logger;

    public ReservationSweeper(IServiceProvider sp, ILogger<ReservationSweeper> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Reservation sweep failed."); }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var fulfil = scope.ServiceProvider.GetRequiredService<IOrderFulfilmentService>();

        var cutoff = DateTime.UtcNow - Ttl;
        var staleOrderIds = await db.StockReservations
            .Where(r => r.CreatedAt < cutoff && !r.Order.IsPaid)
            .Select(r => r.OrderId)
            .Distinct()
            .ToListAsync(ct);

        foreach (var id in staleOrderIds)
        {
            await fulfil.ReleaseReservationAsync(id);
            var order = await db.Orders.FindAsync(new object[] { id }, ct);
            if (order != null && !order.IsPaid && order.Status != OrderStatus.Cancelled)
            {
                order.Status = OrderStatus.Cancelled;
                order.AdminNotes = $"Auto-cancelled {DateTime.UtcNow:yyyy-MM-dd HH:mm}: payment not completed; reserved stock released.";
                await db.SaveChangesAsync(ct);
            }
        }

        if (staleOrderIds.Count > 0)
            _logger.LogInformation("Reservation sweep released {Count} abandoned order(s).", staleOrderIds.Count);
    }
}
