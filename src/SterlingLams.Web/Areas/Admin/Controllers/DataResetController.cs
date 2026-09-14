using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.Areas.Admin.Controllers;

/// <summary>
/// Owner-only "Reset test data" tool. Clears all TRANSACTIONAL data (orders, payments, refunds,
/// stock ledger/transfers/reservations, POS sessions/cash, loyalty, gift-card txns, carts, email log,
/// marketing runs, reviews, expenses, traffic) and then sets every stock record to 2 — so a fresh
/// test round starts clean. Keeps products, categories, customers, stores, registers, staff and
/// settings. Section == null makes this owner-only (see AdminBaseController). Irreversible; gated by a
/// typed "RESET" confirmation.
/// </summary>
public class DataResetController : AdminBaseController
{
    protected override string? Section => null; // owner-only

    private readonly ApplicationDbContext _db;
    private readonly SterlingLams.Web.Services.IStorefrontCache _storefrontCache;
    public DataResetController(ApplicationDbContext db, SterlingLams.Web.Services.IStorefrontCache storefrontCache)
    {
        _db = db;
        _storefrontCache = storefrontCache;
    }

    [HttpGet]
    public IActionResult Index()
    {
        ViewData["Title"] = "Reset test data";
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Run(string confirm, int stockPerItem = 2)
    {
        if (!string.Equals((confirm ?? "").Trim(), "RESET", System.StringComparison.Ordinal))
        {
            TempData["Error"] = "Please type RESET (in capitals) to confirm.";
            return RedirectToAction(nameof(Index));
        }
        stockPerItem = System.Math.Clamp(stockPerItem, 0, 9999);

        await using var tx = await _db.Database.BeginTransactionAsync();

        // Delete children/order-referencing rows first, then Orders, then the standalone parents.
        await _db.RefundItems.ExecuteDeleteAsync();
        await _db.Refunds.ExecuteDeleteAsync();
        await _db.OrderNotes.ExecuteDeleteAsync();
        await _db.OrderPayments.ExecuteDeleteAsync();
        await _db.OrderItems.ExecuteDeleteAsync();
        await _db.PointsLedgerEntries.ExecuteDeleteAsync();
        await _db.GiftCardTransactions.ExecuteDeleteAsync();
        await _db.StockReservations.ExecuteDeleteAsync();
        await _db.StockTransferItems.ExecuteDeleteAsync();
        await _db.StockTransfers.ExecuteDeleteAsync();
        await _db.CashMovements.ExecuteDeleteAsync();
        await _db.ParkedSales.ExecuteDeleteAsync();
        await _db.StockAdjustmentLines.ExecuteDeleteAsync();
        await _db.StockAdjustments.ExecuteDeleteAsync();
        await _db.StockTakeLines.ExecuteDeleteAsync();
        await _db.StockTakes.ExecuteDeleteAsync();
        await _db.StockMovements.ExecuteDeleteAsync();
        await _db.Referrals.ExecuteDeleteAsync();
        await _db.ProductReviews.ExecuteDeleteAsync();
        await _db.BackInStockRequests.ExecuteDeleteAsync();
        await _db.AbandonedCarts.ExecuteDeleteAsync();
        await _db.LabelReprintQueue.ExecuteDeleteAsync();
        await _db.TrafficHits.ExecuteDeleteAsync();
        await _db.CampaignRecipients.ExecuteDeleteAsync();
        await _db.AutomationRuns.ExecuteDeleteAsync();
        await _db.Expenses.ExecuteDeleteAsync();
        await _db.EmailLogs.ExecuteDeleteAsync();
        var orders = await _db.Orders.ExecuteDeleteAsync();
        // TillSessions must go AFTER Orders/Refunds/CashMovements — those carry a TillSessionId FK.
        await _db.TillSessions.ExecuteDeleteAsync();
        await _db.LoyaltyAccounts.ExecuteDeleteAsync();
        await _db.GiftCards.ExecuteDeleteAsync();

        // Reset every stock record: on-hand = chosen amount, reservations cleared.
        var inv = await _db.StoreInventories.ExecuteUpdateAsync(s => s
            .SetProperty(x => x.QuantityOnHand, stockPerItem)
            .SetProperty(x => x.QuantityReserved, 0));

        await tx.CommitAsync();
        try { await _storefrontCache.EvictAsync(); } catch { }

        await LogAsync("Reset", "System", null,
            $"Cleared test data ({orders} orders) and set stock to {stockPerItem} across {inv} inventory records.");
        TempData["Success"] = $"Done — cleared all test transactions ({orders} orders removed) and set every stock record to {stockPerItem} ({inv} updated). You can start a fresh test.";
        return RedirectToAction(nameof(Index));
    }
}
