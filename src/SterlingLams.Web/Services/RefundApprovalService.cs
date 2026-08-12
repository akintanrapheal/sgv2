using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services.Payment;

namespace SterlingLams.Web.Services;

public class RefundApprovalResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}

public interface IRefundApprovalService
{
    /// <summary>Approve a pending refund — this is when the money is actually paid out and the stock,
    /// loyalty/gift-card reversal, gateway refund and order status are applied.</summary>
    Task<RefundApprovalResult> ApproveAsync(int refundId, string approverUserId, string? note = null);
    /// <summary>Reject a pending refund — nothing is paid out or restocked; the quantity frees up.</summary>
    Task<RefundApprovalResult> RejectAsync(int refundId, string approverUserId, string? note = null);
    Task<int> PendingCountAsync();
}

/// <summary>
/// Applies (or rejects) a refund REQUEST once Finance has decided. Creating a refund only records the
/// intent (PendingApproval); no money leaves and no stock moves until <see cref="ApproveAsync"/> runs
/// here — which mirrors the side-effects the old inline refund code used to do immediately.
/// </summary>
public class RefundApprovalService : IRefundApprovalService
{
    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;
    private readonly ILoyaltyService _loyalty;
    private readonly IGiftCardService _giftCards;
    private readonly IPaymentService _payment;
    private readonly IAuditService _audit;
    private readonly ILogger<RefundApprovalService> _log;

    public RefundApprovalService(ApplicationDbContext db, IStockService stock, ILoyaltyService loyalty,
        IGiftCardService giftCards, IPaymentService payment, IAuditService audit, ILogger<RefundApprovalService> log)
    {
        _db = db; _stock = stock; _loyalty = loyalty; _giftCards = giftCards;
        _payment = payment; _audit = audit; _log = log;
    }

    public Task<int> PendingCountAsync() =>
        _db.Refunds.CountAsync(r => r.Status == RefundStatus.PendingApproval);

    public async Task<RefundApprovalResult> ApproveAsync(int refundId, string approverUserId, string? note = null)
    {
        var refund = await _db.Refunds.Include(r => r.Items).FirstOrDefaultAsync(r => r.Id == refundId);
        if (refund == null) return Fail("Refund not found.");
        if (refund.Status != RefundStatus.PendingApproval) return Fail($"This refund is already {refund.Status.ToString().ToLower()}.");

        var order = await _db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == refund.OriginalOrderId);
        if (order == null) return Fail("Original order not found.");

        var now = DateTime.UtcNow;
        string gatewayNote = "";

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            // 1) Stock return (to the branch captured at request time).
            if (refund.RestockRequested && refund.RestockStoreId is int store && store > 0)
                foreach (var it in refund.Items)
                    await _stock.ApplyAsync(it.ProductId, it.ProductVariantId, store, it.Quantity,
                        StockMovementType.Return, refund.RefundNumber, userId: approverUserId);

            // 2) Gateway refund for online orders that were paid through a provider (best-effort).
            if (order.Channel == OrderChannel.Online && !string.IsNullOrEmpty(order.PaymentReference))
            {
                var gw = await _payment.RefundPaymentAsync(new RefundPaymentRequest
                {
                    Reference = order.PaymentReference,
                    Amount = refund.Amount,
                    Reason = refund.Reason
                });
                gatewayNote = gw.Success
                    ? $"gateway refund OK ({gw.ProviderReference ?? "no ref"})"
                    : gw.Supported
                        ? $"gateway refund FAILED — refund manually: {gw.ErrorMessage}"
                        : $"gateway refund not automated — {gw.ErrorMessage}";
            }

            // 3) Order status + the refund's own decision fields.
            if (refund.WasFullRefund) order.Status = OrderStatus.Refunded;
            order.UpdatedAt = now;

            refund.Status = RefundStatus.Approved;
            refund.ApprovedByUserId = approverUserId;
            refund.DecisionAt = now;
            refund.DecisionNote = note;

            var stamp = $"Refund {refund.RefundNumber} approved: ₦{refund.Amount:N0}, {refund.Items.Sum(i => i.Quantity)} item(s)"
                + (refund.RestockRequested ? " (restocked)" : " (no restock)")
                + (gatewayNote.Length > 0 ? $"; {gatewayNote}" : "")
                + (refund.WasFullRefund ? ". Order fully refunded." : ".");
            OrderNotes.AddSystem(_db, order.Id, stamp);

            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            var inner = ex; while (inner.InnerException != null) inner = inner.InnerException;
            _log.LogError(ex, "Refund approval failed for {RefundNumber}", refund.RefundNumber);
            return Fail($"Could not approve the refund: {inner.Message}");
        }

        // 4) On a full refund, reverse loyalty (claw back earned, return redeemed) and gift-card balance.
        //    These run their own saves — kept after commit to match the original refund flow.
        if (refund.WasFullRefund)
        {
            await _loyalty.ReverseForOrderAsync(order.Id);
            await _giftCards.ReverseForOrderAsync(order.Id);
        }

        try
        {
            await _audit.LogAsync("RefundApproved", "Refund", refund.Id.ToString(),
                $"Approved refund {refund.RefundNumber} (₦{refund.Amount:N0}) on {order.OrderNumber}"
                + (gatewayNote.Length > 0 ? $" — {gatewayNote}" : ""), performedBy: approverUserId);
        }
        catch { }

        return new RefundApprovalResult
        {
            Success = true,
            Message = $"Refund {refund.RefundNumber} approved — ₦{refund.Amount:N0} paid out."
                + (gatewayNote.Length > 0 ? $" {gatewayNote}." : "")
        };
    }

    public async Task<RefundApprovalResult> RejectAsync(int refundId, string approverUserId, string? note = null)
    {
        var refund = await _db.Refunds.FirstOrDefaultAsync(r => r.Id == refundId);
        if (refund == null) return Fail("Refund not found.");
        if (refund.Status != RefundStatus.PendingApproval) return Fail($"This refund is already {refund.Status.ToString().ToLower()}.");

        refund.Status = RefundStatus.Rejected;
        refund.ApprovedByUserId = approverUserId;
        refund.DecisionAt = DateTime.UtcNow;
        refund.DecisionNote = note;
        OrderNotes.AddSystem(_db, refund.OriginalOrderId,
            $"Refund {refund.RefundNumber} (₦{refund.Amount:N0}) rejected — not paid out.");
        await _db.SaveChangesAsync();

        try
        {
            await _audit.LogAsync("RefundRejected", "Refund", refund.Id.ToString(),
                $"Rejected refund {refund.RefundNumber} (₦{refund.Amount:N0})"
                + (string.IsNullOrWhiteSpace(note) ? "" : $": {note}"), performedBy: approverUserId);
        }
        catch { }

        return new RefundApprovalResult { Success = true, Message = $"Refund {refund.RefundNumber} rejected — nothing paid out." };
    }

    private static RefundApprovalResult Fail(string msg) => new() { Success = false, Message = msg };
}
