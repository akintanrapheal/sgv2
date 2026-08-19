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

    /// <summary>Refunds whose returned items still need Inventory's restock/write-off decision.</summary>
    Task<int> PendingRestockCountAsync();
    /// <summary>Inventory resolves each returned line: RestockQty units go back on the shelf, the rest
    /// are brought in then written off as damaged (nets to zero, shows in the Shrinkage report) with a
    /// reason. Items not present in the list are left pending.</summary>
    Task<RefundApprovalResult> ResolveStockAsync(int refundId, IReadOnlyList<RestockLineDecision> decisions, string userId);
}

/// <summary>One line's restock decision: how many of the returned units to put back, and (for the
/// remainder) why they're not going back — Damaged, Wrong item sold, etc.</summary>
public record RestockLineDecision(int ItemId, int RestockQty, string? Reason);

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
    private readonly IEmailService _email;
    private readonly ISettingsService _settings;
    private readonly ILogger<RefundApprovalService> _log;

    public RefundApprovalService(ApplicationDbContext db, IStockService stock, ILoyaltyService loyalty,
        IGiftCardService giftCards, IPaymentService payment, IAuditService audit,
        IEmailService email, ISettingsService settings, ILogger<RefundApprovalService> log)
    {
        _email = email; _settings = settings;
        _db = db; _stock = stock; _loyalty = loyalty; _giftCards = giftCards;
        _payment = payment; _audit = audit; _log = log;
    }

    public Task<int> PendingCountAsync() =>
        _db.Refunds.CountAsync(r => r.Status == RefundStatus.PendingApproval);

    public Task<int> PendingRestockCountAsync() =>
        _db.Refunds.CountAsync(r => r.Status == RefundStatus.Approved && r.RestockRequested
            && r.Items.Any(i => i.RestockDecision == RestockDecision.Pending));

    public async Task<RefundApprovalResult> ResolveStockAsync(int refundId, IReadOnlyList<RestockLineDecision> decisions, string userId)
    {
        var refund = await _db.Refunds.Include(r => r.Items).FirstOrDefaultAsync(r => r.Id == refundId);
        if (refund == null) return Fail("Refund not found.");
        if (refund.Status != RefundStatus.Approved) return Fail("Only an approved refund's stock can be resolved.");
        if (!refund.RestockRequested || refund.RestockStoreId is not int store || store <= 0)
            return Fail("This refund has no items to return to stock.");

        var byItem = decisions.Where(d => d != null).GroupBy(d => d.ItemId).ToDictionary(g => g.Key, g => g.Last());
        var now = DateTime.UtcNow;
        int restocked = 0, wroteOff = 0;
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            foreach (var it in refund.Items.Where(i => i.RestockDecision == RestockDecision.Pending))
            {
                if (!byItem.TryGetValue(it.Id, out var d)) continue;   // not decided this round → leave pending
                var restockQty = Math.Clamp(d.RestockQty, 0, it.Quantity);
                var writeOffQty = it.Quantity - restockQty;

                if (restockQty > 0)
                {
                    await _stock.ApplyAsync(it.ProductId, it.ProductVariantId, store, restockQty,
                        StockMovementType.Return, refund.RefundNumber, "Refund restock", userId, materializeVariant: true);
                    restocked += restockQty;
                }
                if (writeOffQty > 0)
                {
                    // Damaged/wrong-item units come back in then get written off, so stock nets to zero and
                    // the loss shows in the Shrinkage report as a Damage movement (carrying the reason).
                    var reason = string.IsNullOrWhiteSpace(d.Reason) ? "Not restocked" : d.Reason!.Trim();
                    await _stock.ApplyAsync(it.ProductId, it.ProductVariantId, store, writeOffQty,
                        StockMovementType.Return, refund.RefundNumber, "Return (in)", userId, materializeVariant: true);
                    await _stock.ApplyAsync(it.ProductId, it.ProductVariantId, store, -writeOffQty,
                        StockMovementType.Damage, refund.RefundNumber, reason, userId);
                    wroteOff += writeOffQty;
                }
                it.RestockedQuantity = restockQty;
                it.RestockNote = writeOffQty > 0 ? (string.IsNullOrWhiteSpace(d.Reason) ? "Not restocked" : d.Reason!.Trim()) : null;
                it.RestockDecision = restockQty > 0 ? RestockDecision.Restocked : RestockDecision.WrittenOff;
                it.RestockDecidedByUserId = userId;
                it.RestockDecidedAt = now;
            }
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            var inner = ex; while (inner.InnerException != null) inner = inner.InnerException;
            _log.LogError(ex, "Refund stock resolution failed for {RefundNumber}", refund.RefundNumber);
            return Fail($"Could not update stock: {inner.Message}");
        }

        if (restocked == 0 && wroteOff == 0) return Fail("Choose restock or damaged for at least one item.");
        try
        {
            await _audit.LogAsync("RefundRestock", "Refund", refund.Id.ToString(),
                $"Resolved {refund.RefundNumber}: {restocked} restocked, {wroteOff} written off (damaged)", performedBy: userId);
        }
        catch { }
        return new RefundApprovalResult { Success = true, Message = $"Updated — {restocked} restocked, {wroteOff} written off as damaged." };
    }

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
            // 1) Stock is NOT returned here. Approving pays out the money; a restock-requested refund
            //    then goes to the Inventory restock queue, where each returned unit is put back or
            //    written off as damaged (Step 2). Items stay RestockDecision.Pending until resolved.

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
                + (refund.RestockRequested ? " (returned items pending Inventory restock/write-off review)" : " (no restock)")
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

        // 5) Tell the branch that returned items are waiting in the restock queue (best-effort).
        if (refund.RestockRequested)
            await NotifyRestockAsync(refund);

        return new RefundApprovalResult
        {
            Success = true,
            Message = $"Refund {refund.RefundNumber} approved — ₦{refund.Amount:N0} paid out."
                + (refund.RestockRequested ? " Returned items sent to Inventory for restock/write-off review." : "")
                + (gatewayNote.Length > 0 ? $" {gatewayNote}." : "")
        };
    }

    /// <summary>Emails the destination branch (+ admin copy) that a refund's returned items are waiting
    /// in Inventory → Returns to restock. Editable template "returns_to_restock". Never throws.</summary>
    private async Task NotifyRestockAsync(Refund refund)
    {
        try
        {
            if (!await _settings.GetBoolAsync("notifications.branch_fulfilment", true)) return;
            var store = refund.RestockStoreId is int sid
                ? await _db.Stores.Where(s => s.Id == sid).Select(s => new { s.Name, s.Email }).FirstOrDefaultAsync()
                : null;
            var branch = (store?.Name ?? "your branch").Replace("Sterlin Glams ", "");

            var subjT = await _settings.GetAsync("email.returns_to_restock.subject", "Returned items to restock at {branch}");
            var introT = await _settings.GetAsync("email.returns_to_restock.intro",
                "A refund has been approved and returned items are waiting at {branch}. Check each one in Inventory → Returns to restock and put it back on the shelf or write it off as damaged.");
            string Fill(string s) => s.Replace("{branch}", branch);

            string E(string s) => System.Net.WebUtility.HtmlEncode(s);
            var rows = string.Concat(refund.Items.Select(i =>
                $"<tr><td style=\"padding:8px 0;border-bottom:1px solid #f0efee;color:#374151;\"><strong style=\"color:#1c1917;\">{E(i.ProductName)}{(i.VariantName != null ? $" ({E(i.VariantName)})" : "")}</strong> &times; {i.Quantity}</td></tr>"));
            var html = $"<h2 style=\"font-size:18px;margin:0 0 12px;\">Returned items to restock</h2>"
                + $"<p style=\"color:#44403c;\">{E(Fill(introT))}</p>"
                + $"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"font-size:14px;border-collapse:collapse;margin:14px 0;\">{rows}</table>"
                + "<p style=\"color:#57534e;font-size:13px;\">Open <strong>Inventory System → Returns to restock</strong> to put each item back on the shelf or write it off as damaged.</p>";

            var subject = Fill(subjT);
            if (!string.IsNullOrWhiteSpace(store?.Email)) await _email.SendAsync(store!.Email!, subject, html);
            var admin = await _settings.GetAsync("notifications.admin_email", "");
            if (!string.IsNullOrWhiteSpace(admin) && !string.Equals(admin, store?.Email, StringComparison.OrdinalIgnoreCase))
                await _email.SendAsync(admin, "[copy] " + subject, html);
        }
        catch (Exception ex) { _log.LogError(ex, "Restock-notify email failed for {RefundNumber}", refund.RefundNumber); }
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
