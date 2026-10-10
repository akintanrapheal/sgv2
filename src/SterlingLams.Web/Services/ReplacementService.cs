using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

public class ReplacementResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public int ReplacementId { get; set; }
}

public interface IReplacementService
{
    /// <summary>Raise a replacement on a paid online order: deduct the replacement item's stock at the
    /// order's branch now, and settle the returned (bad) item per <paramref name="returnDisposition"/> —
    /// restock it now (default), write it off as damaged now, or (Pending) leave it for Inventory's
    /// restock/write-off decision. No money is paid out; any extra for a costlier item is noted.</summary>
    Task<ReplacementResult> CreateAsync(int orderId, int orderItemId, int newProductId, int? newVariantId,
        int newQty, string? reason, decimal balancePaid, string? balanceNote, string userId, string? userName,
        RestockDecision returnDisposition = RestockDecision.Restocked);

    /// <summary>Inventory's Step-2 decision on the returned item: put <paramref name="restockQty"/> back on
    /// the shelf at the branch; write off the rest as damaged (shrinkage) with a reason.</summary>
    Task<ReplacementResult> ResolveStockAsync(int replacementId, int restockQty, string? reason, string userId);

    /// <summary>Replacements whose returned item still needs Inventory's restock/write-off decision.</summary>
    Task<int> PendingDisposalCountAsync();
}

public class ReplacementService : IReplacementService
{
    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;
    private readonly IAuditService _audit;
    private readonly ILogger<ReplacementService> _log;

    public ReplacementService(ApplicationDbContext db, IStockService stock, IAuditService audit,
        ILogger<ReplacementService> log)
    {
        _db = db; _stock = stock; _audit = audit; _log = log;
    }

    public Task<int> PendingDisposalCountAsync() =>
        _db.OrderReplacements.CountAsync(r => r.RestockDecision == RestockDecision.Pending);

    public async Task<ReplacementResult> CreateAsync(int orderId, int orderItemId, int newProductId, int? newVariantId,
        int newQty, string? reason, decimal balancePaid, string? balanceNote, string userId, string? userName,
        RestockDecision returnDisposition = RestockDecision.Restocked)
    {
        if (newQty <= 0) return Fail("Choose how many of the replacement to send (at least 1).");
        if (newProductId <= 0) return Fail("Pick a replacement product.");

        var order = await _db.Orders.Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.Channel == OrderChannel.Online);
        if (order == null) return Fail("Online order not found.");
        if (!order.IsPaid) return Fail("An unpaid order can't have a replacement.");
        if (order.Status == OrderStatus.Refunded) return Fail("This order is fully refunded.");

        var bad = order.Items.FirstOrDefault(i => i.Id == orderItemId);
        if (bad == null) return Fail("The item being replaced isn't on this order.");

        var storeId = order.FulfillingStoreId ?? order.PickupStoreId ?? 0;
        if (storeId <= 0) return Fail("This order has no branch, so stock can't be moved. Add a fulfilling branch first.");

        // Resolve the replacement item's name/variant/SKU (for the record, history, and the line swap below).
        var np = await _db.Products.Where(p => p.Id == newProductId)
            .Select(p => new { p.Id, p.Name, p.Sku }).FirstOrDefaultAsync();
        if (np == null) return Fail("Replacement product not found.");
        string? newVariantName = null;
        string? newSku = np.Sku;
        if (newVariantId is int vid && vid > 0)
        {
            var nv = await _db.ProductVariants.Where(v => v.Id == vid && v.ProductId == newProductId)
                .Select(v => new { v.Name, v.Sku }).FirstOrDefaultAsync();
            if (nv == null) return Fail("Replacement variant not found on that product.");
            newVariantName = nv.Name;
            newSku = string.IsNullOrWhiteSpace(nv.Sku) ? np.Sku : nv.Sku;
        }
        else newVariantId = null;

        var now = DateTime.UtcNow;
        var number = $"REP-{now:yyMMdd}-{now:HHmmssfff}";
        var branch = (await _db.Stores.Where(s => s.Id == storeId).Select(s => s.Name).FirstOrDefaultAsync()
                      ?? "branch").Replace("Sterlin Glams ", "");

        var rep = new OrderReplacement
        {
            ReplacementNumber = number,
            OriginalOrderId = order.Id,
            StoreId = storeId,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            OldProductId = bad.ProductId,
            OldProductVariantId = bad.ProductVariantId,
            OldProductName = bad.ProductName,
            OldVariantName = bad.VariantName,
            // How many of the bad line come back — the replacement count, capped at what was ordered.
            OldQuantity = Math.Max(1, Math.Min(newQty, bad.Quantity)),
            NewProductId = np.Id,
            NewProductVariantId = newVariantId,
            NewProductName = np.Name,
            NewVariantName = newVariantName,
            NewQuantity = newQty,
            BalancePaid = Math.Max(0, balancePaid),
            BalanceNote = string.IsNullOrWhiteSpace(balanceNote) ? null : balanceNote.Trim(),
            RestockDecision = returnDisposition,
            CreatedByUserId = userId,
            CreatedByName = userName,
            CreatedAt = now,
        };

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            // Deduct the replacement item's stock at the order's branch now (it's physically going out).
            await _stock.ApplyAsync(np.Id, newVariantId, storeId, -newQty,
                StockMovementType.Sale, order.OrderNumber,
                $"Replacement {number} for {bad.ProductName}{(bad.VariantName != null ? $" ({bad.VariantName})" : "")}",
                userId, materializeVariant: true);

            _db.OrderReplacements.Add(rep);

            // Settle the returned (bad) item now per the staff choice: put it straight back on the shelf
            // (restock), or write it off as damaged. Pending = leave it for Inventory → Returns to decide.
            string returnedFate;
            if (returnDisposition == RestockDecision.Restocked)
            {
                await _stock.ApplyAsync(rep.OldProductId, rep.OldProductVariantId, storeId, rep.OldQuantity,
                    StockMovementType.Return, number, "Replacement return restock", userId, materializeVariant: true);
                rep.RestockedQuantity = rep.OldQuantity;
                rep.RestockDecidedByUserId = userId;
                rep.RestockDecidedAt = now;
                returnedFate = $"returned item restocked at {branch}";
            }
            else if (returnDisposition == RestockDecision.WrittenOff)
            {
                // Comes back in then straight out as Damage, so stock nets to zero and the loss shows in
                // the Shrinkage report (carrying the reason).
                var rz = rep.Reason ?? "Damaged (replacement return)";
                await _stock.ApplyAsync(rep.OldProductId, rep.OldProductVariantId, storeId, rep.OldQuantity,
                    StockMovementType.Return, number, "Return (in)", userId, materializeVariant: true);
                await _stock.ApplyAsync(rep.OldProductId, rep.OldProductVariantId, storeId, -rep.OldQuantity,
                    StockMovementType.Damage, number, rz, userId);
                rep.RestockedQuantity = 0;
                rep.RestockNote = rz;
                rep.RestockDecidedByUserId = userId;
                rep.RestockDecidedAt = now;
                returnedFate = "returned item written off as damaged";
            }
            else
            {
                returnedFate = "returned item pending Inventory restock/write-off review";
            }

            var newLabel = $"{np.Name}{(newVariantName != null ? $" ({newVariantName})" : "")}";
            var oldLabel = $"{bad.ProductName}{(bad.VariantName != null ? $" ({bad.VariantName})" : "")}";

            // Swap the order LINE to the replacement item so every screen that reads the order (admin order
            // detail, the EPOS pack/collect view, receipts, labels) shows what the customer is actually
            // getting — not the item they no longer want. Only for a clean whole-line 1:1 swap; price and
            // quantity are kept so the paid total is unchanged (replacements never refund). The original
            // item is preserved on the OrderReplacement record + the note below.
            var lineSwapped = rep.OldQuantity == bad.Quantity && newQty == bad.Quantity;
            if (lineSwapped)
            {
                bad.ProductId = np.Id;
                bad.ProductVariantId = newVariantId;
                bad.ProductName = np.Name;
                bad.VariantName = newVariantName;
                bad.ProductSku = newSku;
            }

            OrderNotes.AddSystem(_db, order.Id,
                $"Replacement {number}: {oldLabel} ×{rep.OldQuantity} → {newLabel} ×{newQty}"
                + (rep.Reason != null ? $" ({rep.Reason})" : "")
                + $". New item stock deducted at {branch}; {returnedFate}."
                + (lineSwapped ? " Order line updated to the new item." : " Order line left unchanged (partial/qty-mismatch replacement).")
                + (rep.BalancePaid > 0 ? $" Balance paid ₦{rep.BalancePaid:N0}{(rep.BalanceNote != null ? $" — {rep.BalanceNote}" : "")}." : ""));

            order.UpdatedAt = now;
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (InsufficientStockException)
        {
            await tx.RollbackAsync();
            return Fail($"Not enough stock of {np.Name} at {branch} to send as a replacement.");
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            var inner = ex; while (inner.InnerException != null) inner = inner.InnerException;
            _log.LogError(ex, "Replacement failed for order {OrderId}", orderId);
            return Fail($"Could not raise the replacement: {inner.Message}");
        }

        try
        {
            await _audit.LogAsync("ReplacementRaised", "Order", order.Id.ToString(),
                $"Replacement {number} on {order.OrderNumber}: {bad.ProductName} → {np.Name} ×{newQty}", performedBy: userId);
        }
        catch { }

        var tail = returnDisposition switch
        {
            RestockDecision.Restocked  => $"The returned item was restocked at {branch}.",
            RestockDecision.WrittenOff => "The returned item was written off as damaged.",
            _                          => "The returned item is in Inventory → Returns for a put-back / write-off decision.",
        };
        return new ReplacementResult
        {
            Success = true,
            ReplacementId = rep.Id,
            Message = $"Replacement {number} raised — {np.Name} ×{newQty} deducted at {branch}. " + tail
        };
    }

    public async Task<ReplacementResult> ResolveStockAsync(int replacementId, int restockQty, string? reason, string userId)
    {
        var rep = await _db.OrderReplacements.FirstOrDefaultAsync(r => r.Id == replacementId);
        if (rep == null) return Fail("Replacement not found.");
        if (rep.RestockDecision != RestockDecision.Pending) return Fail("This returned item has already been resolved.");
        if (rep.StoreId <= 0) return Fail("This replacement has no branch to return stock to.");

        var restock = Math.Clamp(restockQty, 0, rep.OldQuantity);
        var writeOff = rep.OldQuantity - restock;
        var now = DateTime.UtcNow;

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            if (restock > 0)
            {
                await _stock.ApplyAsync(rep.OldProductId, rep.OldProductVariantId, rep.StoreId, restock,
                    StockMovementType.Return, rep.ReplacementNumber, "Replacement return restock", userId, materializeVariant: true);
            }
            if (writeOff > 0)
            {
                // Damaged units come back in then get written off, so stock nets to zero and the loss shows
                // in the Shrinkage report as a Damage movement (carrying the reason).
                var rz = string.IsNullOrWhiteSpace(reason) ? "Damaged (replacement return)" : reason!.Trim();
                await _stock.ApplyAsync(rep.OldProductId, rep.OldProductVariantId, rep.StoreId, writeOff,
                    StockMovementType.Return, rep.ReplacementNumber, "Return (in)", userId, materializeVariant: true);
                await _stock.ApplyAsync(rep.OldProductId, rep.OldProductVariantId, rep.StoreId, -writeOff,
                    StockMovementType.Damage, rep.ReplacementNumber, rz, userId);
            }

            rep.RestockedQuantity = restock;
            rep.RestockNote = writeOff > 0 ? (string.IsNullOrWhiteSpace(reason) ? "Damaged" : reason!.Trim()) : null;
            rep.RestockDecision = restock > 0 ? RestockDecision.Restocked : RestockDecision.WrittenOff;
            rep.RestockDecidedByUserId = userId;
            rep.RestockDecidedAt = now;

            OrderNotes.AddSystem(_db, rep.OriginalOrderId,
                $"Replacement {rep.ReplacementNumber} returned item resolved: {restock} restocked"
                + (writeOff > 0 ? $", {writeOff} written off as damaged" : "") + ".");

            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            var inner = ex; while (inner.InnerException != null) inner = inner.InnerException;
            _log.LogError(ex, "Replacement stock resolution failed for {Number}", rep.ReplacementNumber);
            return Fail($"Could not update stock: {inner.Message}");
        }

        try
        {
            await _audit.LogAsync("ReplacementRestock", "OrderReplacement", rep.Id.ToString(),
                $"Resolved {rep.ReplacementNumber}: {restock} restocked, {writeOff} written off", performedBy: userId);
        }
        catch { }

        return new ReplacementResult { Success = true, Message = $"Updated — {restock} restocked, {writeOff} written off as damaged." };
    }

    private static ReplacementResult Fail(string msg) => new() { Success = false, Message = msg };
}
