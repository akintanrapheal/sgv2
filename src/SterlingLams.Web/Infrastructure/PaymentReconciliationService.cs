using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using SterlingLams.Web.Services.Payment;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Catches online orders whose payment actually succeeded at the gateway but never got captured on
/// our side — the browser callback didn't run (customer closed the tab / lost the redirect back from
/// their bank app) AND the webhook was missed. Without this, such an order sits unpaid + Pending
/// until the <see cref="ReservationSweeper"/> auto-cancels it, and someone has to confirm it by hand
/// from the Paystack dashboard.
///
/// Every few minutes it re-verifies recent unpaid online orders that carry a gateway reference
/// (stored at payment initiation) against the provider's verify API, and captures any the gateway
/// reports as paid — the same confirm/fulfil path the webhook uses. Idempotent and self-healing.
/// </summary>
public class PaymentReconciliationService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(3);
    // Only look back a bounded window so we don't re-verify ancient abandoned orders forever. Orders
    // are auto-cancelled ~60 min after being placed, so a day's window comfortably covers the gap.
    private static readonly TimeSpan Lookback = TimeSpan.FromHours(24);

    private readonly IServiceProvider _sp;
    private readonly ILogger<PaymentReconciliationService> _logger;

    public PaymentReconciliationService(IServiceProvider sp, ILogger<PaymentReconciliationService> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small initial delay so the app finishes starting before the first sweep.
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Payment reconciliation sweep failed."); }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var since = DateTime.UtcNow - Lookback;

        List<int> candidateIds;
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            // Unpaid, still-pending online orders that have a gateway reference (i.e. they went to the
            // payment gateway — customer-care "pay later" orders have no reference and are skipped).
            candidateIds = await db.Orders
                .Where(o => o.Channel == OrderChannel.Online
                         && !o.IsPaid
                         && o.Status == OrderStatus.Pending
                         && o.PlacedByStaffUserId == null
                         && o.PaymentReference != null && o.PaymentReference != ""
                         && o.CreatedAt >= since)
                .Select(o => o.Id)
                .ToListAsync(ct);
        }

        if (candidateIds.Count == 0) return;

        var recovered = 0;
        foreach (var id in candidateIds)
        {
            if (ct.IsCancellationRequested) break;
            // Each order in its own scope so one failure can't poison the next.
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var payment = scope.ServiceProvider.GetRequiredService<IPaymentService>();
            var capture = scope.ServiceProvider.GetRequiredService<IPaymentCaptureService>();

            var order = await db.Orders.AsNoTracking()
                .Where(o => o.Id == id)
                .Select(o => new { o.Id, o.OrderNumber, o.PaymentReference, o.IsPaid, o.Status })
                .FirstOrDefaultAsync(ct);
            if (order == null || order.IsPaid || order.Status != OrderStatus.Pending
                || string.IsNullOrWhiteSpace(order.PaymentReference))
                continue;

            try
            {
                var v = await payment.VerifyPaymentAsync(order.PaymentReference);
                if (!v.IsPaid) continue; // genuinely unpaid — leave it for the sweeper to cancel.

                var result = await capture.CaptureAsync(order.Id,
                    string.IsNullOrWhiteSpace(v.Reference) ? order.PaymentReference : v.Reference!,
                    v.AmountPaid, payment.ProviderName, ct);

                if (result == PaymentCaptureResult.Captured)
                {
                    recovered++;
                    _logger.LogWarning(
                        "Reconciled order {OrderNumber}: payment was successful at {Provider} but never captured — now confirmed.",
                        order.OrderNumber, payment.ProviderName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Payment reconciliation failed for order {OrderNumber} ({Id}).", order.OrderNumber, id);
            }
        }

        if (recovered > 0)
            _logger.LogInformation("Payment reconciliation captured {Count} previously-stranded paid order(s).", recovered);
    }
}
