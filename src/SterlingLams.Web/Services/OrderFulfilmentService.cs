using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services.Payment;

namespace SterlingLams.Web.Services;

/// <summary>Result of trying to fulfil a paid order. Stock is no longer held before payment
/// (no reservations) — it's committed first-come-first-served at payment time.</summary>
public enum FulfilOutcome
{
    /// <summary>Stock committed (or order already fulfilled / not applicable).</summary>
    Fulfilled,
    /// <summary>An item sold out before this payment landed — caller should cancel + refund.</summary>
    SoldOut,
    /// <summary>Transient failure (e.g. concurrency/DB) — left for the retry service, do NOT refund.</summary>
    Deferred
}

public interface IOrderFulfilmentService
{
    /// <summary>Frees any legacy reservation rows for an order (no-op now that orders don't reserve
    /// before payment; kept for the abandoned-order sweeper + failed-payment path).</summary>
    Task ReleaseReservationAsync(int orderId);

    /// <summary>Fulfils a paid online order against the in-house stock ledger, committing stock
    /// first-come-first-served. Idempotent and safe to call from every payment-confirmation path
    /// (browser callback and webhook). Returns SoldOut if an item is no longer available.</summary>
    Task<FulfilOutcome> FulfilPaidOrderAsync(int orderId);
}

public class OrderFulfilmentService : IOrderFulfilmentService
{
    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;
    private readonly ILogger<OrderFulfilmentService> _logger;
    private readonly IPaymentService _payment;
    private readonly IEmailService _email;

    public OrderFulfilmentService(ApplicationDbContext db, IStockService stock,
        ILogger<OrderFulfilmentService> logger, IPaymentService payment, IEmailService email)
    {
        _db = db;
        _stock = stock;
        _logger = logger;
        _payment = payment;
        _email = email;
    }

    // Variant-level stock: an order line for variant V at store S draws on V's own inventory row
    // if one exists there, otherwise the shared product pool row (ProductVariantId == null). This
    // resolver maps a requested (product, variant, store) to that EFFECTIVE row's variant id, so
    // availability/reservation/deduction all agree — and two un-stocked variants that both fall
    // back to the same pool share one counter (no oversell).
    private static Func<int, int?, int, int?> EffectiveVariantResolver(IEnumerable<StoreInventory> rows)
    {
        var variantRows = rows.Where(r => r.ProductVariantId != null)
            .Select(r => (r.ProductId, Vid: r.ProductVariantId!.Value, r.StoreId))
            .ToHashSet();
        return (pid, vid, sid) =>
            vid.HasValue && variantRows.Contains((pid, vid.Value, sid)) ? vid : (int?)null;
    }

    // ── Allocation ────────────────────────────────────────────────────────────
    // Spreads an order's lines across branches: the fulfilment branch first (pickup store, or
    // nearest to the customer), then the next-nearest. `avail(product, variant, store)` supplies the
    // usable quantity of the effective row; `effVid` collapses lines that share a pool so they draw
    // on one counter. Returns the per-(store, product, variant) allocation and the first line that
    // couldn't be fully covered (null = success).
    private static (Store fulfilStore, Dictionary<(int store, int product, int? variant), int> alloc, OrderItem? shortLine)
        Allocate(Order order, List<Store> activeStores, List<Store> ranked,
            Func<int, int?, int, int?> effVid, Func<int, int?, int, int> avail)
    {
        Store fulfilStore = (order.FulfillmentType == FulfillmentType.StorePickup
                ? activeStores.FirstOrDefault(s => s.Id == order.PickupStoreId)
                : null)
            ?? ranked.First();

        var storeOrder = new List<int> { fulfilStore.Id };
        storeOrder.AddRange(ranked.Where(s => s.Id != fulfilStore.Id).Select(s => s.Id));

        // Keyed by the EFFECTIVE row so several lines sharing a pool decrement the same balance.
        var remaining = new Dictionary<(int product, int? effVid, int store), int>();
        int Remaining(int pid, int? vid, int sid)
        {
            var k = (pid, effVid(pid, vid, sid), sid);
            if (!remaining.TryGetValue(k, out var v)) { v = avail(pid, vid, sid); remaining[k] = v; }
            return v;
        }

        var alloc = new Dictionary<(int store, int product, int? variant), int>();
        foreach (var line in order.Items)
        {
            var need = line.Quantity;
            foreach (var sid in storeOrder)
            {
                if (need <= 0) break;
                var take = Math.Min(need, Remaining(line.ProductId, line.ProductVariantId, sid));
                if (take <= 0) continue;
                remaining[(line.ProductId, effVid(line.ProductId, line.ProductVariantId, sid), sid)] -= take;
                alloc.TryGetValue((sid, line.ProductId, line.ProductVariantId), out var acc);
                alloc[(sid, line.ProductId, line.ProductVariantId)] = acc + take;
                need -= take;
            }
            if (need > 0) return (fulfilStore, alloc, line);
        }
        return (fulfilStore, alloc, null);
    }

    // ── Concurrency helper ───────────────────────────────────────────────────
    /// <summary>
    /// Acquires Postgres row locks (SELECT ... FOR UPDATE) on the given product+store inventory
    /// rows (all variant rows + the pool for each pair, since the predicate omits the variant),
    /// in a fixed (ProductId, StoreId) order. Serializes concurrent reservations/sales/transfers
    /// instead of racing on stale snapshots. Caller must already be inside a transaction.
    /// </summary>
    private async Task LockInventoryRowsAsync(IEnumerable<(int ProductId, int StoreId)> pairs)
    {
        if (!_db.Database.IsNpgsql()) return; // FOR UPDATE is Postgres-only (SQLite test harness no-ops)
        foreach (var (pid, sid) in pairs.Distinct().OrderBy(p => p.ProductId).ThenBy(p => p.StoreId))
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"StoreInventories\" WHERE \"ProductId\" = {pid} AND \"StoreId\" = {sid} FOR UPDATE");
    }

    // ── Release (legacy holds + abandoned-order sweeper) ────────────────────────
    public async Task ReleaseReservationAsync(int orderId)
    {
        var rows = await _db.StockReservations.Where(r => r.OrderId == orderId).ToListAsync();
        if (rows.Count == 0) return;
        await using var tx = await _db.Database.BeginTransactionAsync();
        await ReleaseRowsAsync(rows);
        await tx.CommitAsync();
    }

    /// <summary>Releases reservation rows and their QuantityReserved holds (on the effective row).
    /// Caller must already be inside a transaction.</summary>
    private async Task ReleaseRowsAsync(List<StockReservation> rows)
    {
        await LockInventoryRowsAsync(rows.Select(r => (r.ProductId, r.StoreId)));

        var pids = rows.Select(r => r.ProductId).Distinct().ToList();
        var sids = rows.Select(r => r.StoreId).Distinct().ToList();
        var invRows = await _db.StoreInventories
            .Where(si => pids.Contains(si.ProductId) && sids.Contains(si.StoreId))
            .ToListAsync();
        var invMap = invRows.ToDictionary(si => (si.ProductId, si.ProductVariantId, si.StoreId));
        var effVid = EffectiveVariantResolver(invRows);
        foreach (var r in rows)
            if (invMap.TryGetValue((r.ProductId, effVid(r.ProductId, r.ProductVariantId, r.StoreId), r.StoreId), out var si))
                si.QuantityReserved = Math.Max(0, si.QuantityReserved - r.Quantity);
        _db.StockReservations.RemoveRange(rows);
        await _db.SaveChangesAsync();
    }

    // ── Fulfil ─────────────────────────────────────────────────────────────────
    /// <summary>
    /// Picks the branch nearest the customer, transfers in any units it lacks from other
    /// branches (transfer-then-sell so every branch balance stays correct), then sells the
    /// whole order from that branch. Stock is NOT held before payment — it's committed
    /// first-come-first-served here, under a row lock, so a simultaneous payment for the last
    /// unit serialises and the loser gets SoldOut (caller refunds).
    /// Variant-aware: allocation/transfer/sale all run against each line's effective row.
    /// Idempotent. Failures are logged, never thrown — the customer has already paid.
    /// </summary>
    public async Task<FulfilOutcome> FulfilPaidOrderAsync(int orderId)
    {
        try
        {
            var order = await _db.Orders
                .Include(o => o.Items)
                .Include(o => o.PickupStore)
                .Include(o => o.DeliveryAddress)
                .FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null) return FulfilOutcome.Fulfilled;

            // Only online orders are fulfilled through this multi-branch pipeline. POS sales are
            // already settled (stock deducted) at the till — never re-fulfil one.
            if (order.Channel != OrderChannel.Online) return FulfilOutcome.Fulfilled;

            // Idempotency: a fulfilled order already has its branch + ledger movements.
            if (order.FulfillingStoreId != null) return FulfilOutcome.Fulfilled;
            // Terminal already (e.g. a prior callback/webhook already refunded a sold-out order) —
            // never re-process, so we can't double-refund.
            if (order.Status is OrderStatus.Cancelled or OrderStatus.Refunded) return FulfilOutcome.Fulfilled;

            var activeStores = await _db.Stores.Where(s => s.IsActive).ToListAsync();
            if (activeStores.Count == 0)
            {
                _logger.LogError("No active stores — cannot fulfil order {OrderNumber}.", order.OrderNumber);
                return FulfilOutcome.Deferred;
            }
            var ranked = DeliveryZoneService.RankStoresByProximity(
                activeStores, order.DeliveryAddress?.State, order.DeliveryAddress?.City);

            var productIds = order.Items.Select(i => i.ProductId).Distinct().ToList();
            var storeIds = activeStores.Select(s => s.Id).ToList();
            var products = await _db.Products.Where(p => productIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Name);
            var now = DateTime.UtcNow;

            // Lock the candidate (product, store) rows for the whole allocate→deduct so two
            // concurrent payments for the same last unit can't both succeed. Nothing is written
            // on the sold-out path, so the lock is released cleanly before we refund.
            var soldOut = false;
            await using (var tx = await _db.Database.BeginTransactionAsync())
            {
                await LockInventoryRowsAsync(productIds.SelectMany(pid => storeIds.Select(sid => (pid, sid))));

                var invRows = await _db.StoreInventories
                    .Where(si => productIds.Contains(si.ProductId) && storeIds.Contains(si.StoreId))
                    .ToListAsync();
                var invMap = invRows.ToDictionary(si => (si.ProductId, si.ProductVariantId, si.StoreId));
                var effVid = EffectiveVariantResolver(invRows);
                int SaleAvail(int pid, int? vid, int sid)
                {
                    var ev = effVid(pid, vid, sid);
                    return invMap.TryGetValue((pid, ev, sid), out var si)
                        ? Math.Max(0, si.QuantityOnHand - si.QuantityReserved) : 0;
                }

                var (fulfilStore, alloc, shortLine) = Allocate(order, activeStores, ranked, effVid, SaleAvail);
                if (shortLine != null)
                {
                    _logger.LogWarning("Order {OrderNumber} sold out before its payment landed — product {ProductId}.",
                        order.OrderNumber, shortLine.ProductId);
                    soldOut = true;
                }
                else
                {
                    // Transfers: every allocation that isn't already at the fulfilment branch moves in,
                    // per (product, variant). ApplyAsync resolves each side to its effective row.
                    foreach (var bySource in alloc.Where(kv => kv.Key.store != fulfilStore.Id).GroupBy(kv => kv.Key.store))
                    {
                        var sourceStore = activeStores.First(s => s.Id == bySource.Key);
                        var transferNumber = $"TRF-{now:yyMMdd}-{now:HHmmssfff}-{sourceStore.Id}";
                        var transfer = new StockTransfer
                        {
                            TransferNumber = transferNumber,
                            FromStoreId = sourceStore.Id,
                            ToStoreId = fulfilStore.Id,
                            Status = TransferStatus.Completed,
                            CreatedByUserId = order.UserId,
                            Note = $"Online order {order.OrderNumber}",
                            CreatedAt = now,
                            ApprovedAt = now,
                            DispatchedAt = now,
                            ReceivedAt = now
                        };
                        foreach (var kv in bySource)
                        {
                            var pid = kv.Key.product;
                            var vid = kv.Key.variant;
                            var qty = kv.Value;
                            transfer.Items.Add(new StockTransferItem
                            {
                                ProductId = pid,
                                ProductVariantId = vid,
                                ProductName = products.GetValueOrDefault(pid, $"#{pid}"),
                                RequestedQty = qty,
                                ApprovedQty = qty,
                                DispatchedQty = qty,
                                ReceivedQty = qty
                            });
                            await _stock.ApplyAsync(pid, vid, sourceStore.Id, -qty, StockMovementType.Transfer,
                                transferNumber, $"To {fulfilStore.Name} (order {order.OrderNumber})", order.UserId);
                            await _stock.ApplyAsync(pid, vid, fulfilStore.Id, qty, StockMovementType.Transfer,
                                transferNumber, $"From {sourceStore.Name} (order {order.OrderNumber})", order.UserId);
                        }
                        _db.StockTransfers.Add(transfer);
                    }

                    // Sell the whole order from the fulfilment branch (consolidated there now).
                    foreach (var line in order.Items)
                        await _stock.ApplyAsync(line.ProductId, line.ProductVariantId, fulfilStore.Id,
                            -line.Quantity, StockMovementType.Sale, order.OrderNumber,
                            $"Online order {order.OrderNumber}", order.UserId);

                    order.FulfillingStoreId = fulfilStore.Id;
                    order.Status = order.FulfillmentType == FulfillmentType.StorePickup
                        ? OrderStatus.ReadyForPickup
                        : OrderStatus.Processing;
                    await _db.SaveChangesAsync();
                    await tx.CommitAsync();

                    _logger.LogInformation(
                        "Order {OrderNumber} fulfilled from {Store} ({Transfers} transfer(s)).",
                        order.OrderNumber, fulfilStore.Name,
                        alloc.Count(kv => kv.Key.store != fulfilStore.Id));
                }
            } // tx disposed — sold-out path wrote nothing, so the hold is released cleanly

            if (soldOut)
            {
                await RefundSoldOutAsync(order);
                return FulfilOutcome.SoldOut;
            }
            return FulfilOutcome.Fulfilled;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fulfilment failed for order {OrderId}: {Message}", orderId, ex.Message);
            // Best-effort: record the failure on the order so it's visible in admin and picked up by
            // the retry service. The transaction above rolled back, so clear the tracker first.
            try
            {
                _db.ChangeTracker.Clear();
                var o = await _db.Orders.FindAsync(orderId);
                if (o != null && o.FulfillingStoreId == null)
                {
                    o.AdminNotes = $"Fulfilment error {DateTime.UtcNow:yyyy-MM-dd HH:mm}: {ex.Message}";
                    await _db.SaveChangesAsync();
                }
            }
            catch { /* never throw from fulfilment — the customer has already paid */ }
            return FulfilOutcome.Deferred; // transient — retry service will pick it up (no refund)
        }
    }

    // Cancel + refund a paid order whose item sold out before its payment landed (the "first to
    // pay wins" loser is made whole). Best-effort; runs from every payment path (callback/webhook/
    // retry) and is guarded by the terminal-status check above so it can't double-refund.
    private async Task RefundSoldOutAsync(Order order)
    {
        order.Status = OrderStatus.Cancelled;
        string note;
        try
        {
            var refund = await _payment.RefundPaymentAsync(new RefundPaymentRequest
            {
                Reference = order.PaymentReference ?? string.Empty,
                Amount = order.Total,
                Reason = "Item sold out before payment completed"
            });
            if (refund.Success)
            {
                order.Status = OrderStatus.Refunded;
                note = $"Auto-refunded {DateTime.UtcNow:yyyy-MM-dd HH:mm}: item sold out before payment landed.";
            }
            else
            {
                note = $"SOLD OUT after payment {DateTime.UtcNow:yyyy-MM-dd HH:mm} — "
                     + (refund.Supported ? $"auto-refund FAILED ({refund.ErrorMessage})" : $"{_payment.ProviderName} has no auto-refund")
                     + "; refund MANUALLY.";
            }
        }
        catch (Exception ex)
        {
            note = $"SOLD OUT after payment {DateTime.UtcNow:yyyy-MM-dd HH:mm} — refund error ({ex.Message}); refund MANUALLY.";
        }
        order.AdminNotes = note;
        await _db.SaveChangesAsync();

        try
        {
            var email = await _db.Users.Where(u => u.Id == order.UserId).Select(u => u.Email).FirstOrDefaultAsync();
            if (!string.IsNullOrEmpty(email))
                await _email.SendAsync(email, "Your Sterlin Glams order could not be completed",
                    $"<p>We're so sorry — an item in your order <strong>{order.OrderNumber}</strong> sold out just before your payment completed, so we couldn't fulfil it.</p>"
                  + $"<p>Your payment of ₦{order.Total:N0} has been refunded in full. Refunds typically settle within a few business days.</p>"
                  + "<p>Please accept our apologies — you're welcome to reorder if it comes back in stock.</p>");
        }
        catch (Exception ex) { _logger.LogError(ex, "Apology email failed for sold-out order {OrderNumber}", order.OrderNumber); }
    }
}
