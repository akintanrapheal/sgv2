using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

public enum PaymentCaptureResult
{
    /// <summary>The order was unpaid and is now confirmed + fulfilled.</summary>
    Captured,
    /// <summary>Already paid — nothing to do (idempotent no-op).</summary>
    AlreadyPaid,
    /// <summary>No such order.</summary>
    NotFound,
    /// <summary>The amount paid is less than the order total — flagged for review, not confirmed.</summary>
    AmountMismatch,
    /// <summary>An item sold out before this payment landed — the order was auto-cancelled + refunded.</summary>
    SoldOut,
}

/// <summary>
/// Single source of truth for turning a *verified* gateway payment into a confirmed, fulfilled
/// order: marks it paid, records the tender note, runs in-house fulfilment (stock + transfers),
/// redeems/accrues loyalty &amp; gift cards, pushes to logistics, and sends the new-order emails.
/// Idempotent — guarded on <c>Order.IsPaid</c>, so the browser callback, the Paystack webhook and
/// the reconciliation sweep can all call it for the same payment and only the first one does the work.
/// </summary>
public interface IPaymentCaptureService
{
    Task<PaymentCaptureResult> CaptureAsync(int orderId, string reference, decimal amountPaid, string provider, CancellationToken ct = default);
}

public class PaymentCaptureService : IPaymentCaptureService
{
    private readonly ApplicationDbContext _db;
    private readonly IOrderFulfilmentService _fulfilment;
    private readonly ILoyaltyService _loyalty;
    private readonly IGiftCardService _giftCards;
    private readonly Logistics.ILogisticsDispatchService _logistics;
    private readonly IAuditService _audit;
    private readonly IWhatsAppService _whatsapp;
    private readonly IPostHogClient _posthog;
    private readonly IOrderNotificationService _notifications;
    private readonly ILogger<PaymentCaptureService> _logger;

    public PaymentCaptureService(
        ApplicationDbContext db,
        IOrderFulfilmentService fulfilment,
        ILoyaltyService loyalty,
        IGiftCardService giftCards,
        Logistics.ILogisticsDispatchService logistics,
        IAuditService audit,
        IWhatsAppService whatsapp,
        IPostHogClient posthog,
        IOrderNotificationService notifications,
        ILogger<PaymentCaptureService> logger)
    {
        _db = db;
        _fulfilment = fulfilment;
        _loyalty = loyalty;
        _giftCards = giftCards;
        _logistics = logistics;
        _audit = audit;
        _whatsapp = whatsapp;
        _posthog = posthog;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task<PaymentCaptureResult> CaptureAsync(int orderId, string reference, decimal amountPaid, string provider, CancellationToken ct = default)
    {
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order == null) return PaymentCaptureResult.NotFound;
        if (order.IsPaid) return PaymentCaptureResult.AlreadyPaid;

        // Verify the amount actually paid covers what we charged. Callers have already proven the
        // payment is genuine (HMAC webhook / server-side verify); this guards against a stale or
        // partial charge marking a full order as paid.
        if (amountPaid + 0.01m < order.Total)
        {
            _logger.LogWarning("Payment amount mismatch for {OrderNumber}: paid {Paid} but order total is {Total}",
                order.OrderNumber, amountPaid, order.Total);
            order.AdminNotes = $"[{DateTime.UtcNow:u}] Payment amount mismatch: paid ₦{amountPaid:N2} vs total ₦{order.Total:N2}. Needs review; not auto-fulfilled.";
            await _db.SaveChangesAsync(ct);
            return PaymentCaptureResult.AmountMismatch;
        }

        var prevStatus = order.Status;
        order.IsPaid = true;
        order.PaidAt = DateTime.UtcNow;
        order.Status = OrderStatus.Confirmed;
        order.PaymentReference = reference;
        order.PaymentProvider = provider;
        OrderNotes.AddSystem(_db, order.Id,
            $"Payment via {provider} successful (Transaction Reference: {reference}).");
        if (prevStatus != OrderStatus.Confirmed)
            OrderNotes.AddSystem(_db, order.Id,
                $"Order status changed from {prevStatus} to Confirmed (payment received).");
        await _db.SaveChangesAsync(ct);

        try { await _audit.LogAsync("Payment", "Order", order.Id.ToString(), $"Payment received for {order.OrderNumber} — ₦{order.Total:N0} ({provider})"); } catch { }
        _ = _whatsapp.NotifyOrderAsync(order.Id, WhatsAppOrderEvent.PaymentReceived);
        // Authoritative funnel completion — fires whether or not the shopper returned to the site.
        await _posthog.CaptureAsync(order.UserId ?? $"order_{order.OrderNumber}", "order_paid", new
        {
            value = order.Total, currency = "NGN", order_number = order.OrderNumber,
            payment_provider = provider, channel = "online",
        });

        // Deduct stock through the in-house ledger. Idempotent (no-ops once FulfillingStoreId is set).
        // If an item sold out before this payment landed, the service auto-cancels + refunds.
        var outcome = await _fulfilment.FulfilPaidOrderAsync(order.Id);
        if (outcome == FulfilOutcome.SoldOut)
        {
            _logger.LogWarning("Order {OrderNumber} sold out before its captured payment — auto-refunded.", order.OrderNumber);
            return PaymentCaptureResult.SoldOut;
        }

        await _loyalty.RedeemForOrderAsync(order.Id);
        await _giftCards.RedeemForOrderAsync(order.Id);
        await _loyalty.AccrueForOrderAsync(order.Id);
        await _logistics.PushOrderAsync(order.Id);

        // Customer confirmation + admin new-order alert. Important for the webhook / reconciliation
        // paths: when the shopper never made it back to the site, this is the only place these go out.
        await _notifications.SendNewOrderEmailsAsync(order.Id, ct);

        _logger.LogInformation("Order {OrderNumber} captured as paid ({Provider}).", order.OrderNumber, provider);
        return PaymentCaptureResult.Captured;
    }
}
