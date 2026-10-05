using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

// ── EposNow-style "Sales & Operation" cash-up summary ──────────────────────────────────────────
// Shared by the POS close screen (PosController) and the read-only Inventory oversight view
// (Inventory/Till/Eod). Pure data build — no HTTP context — so it can run from either area.
public record TenderLine(string Key, string Label, decimal Expected, decimal? Counted)
{ public decimal Variance => (Counted ?? 0) - Expected; }
public record SalesGroupRow(string Name, int Qty, decimal Discount, decimal Net, decimal Tax)
{ public decimal Total => Net + Tax; }
public record TaxGroupRow(string Name, decimal Rate, int Qty, decimal Net, decimal Tax)
{ public decimal Total => Net + Tax; }
public record VoidGroupRow(string Product, string Type, string Staff, int Qty, decimal Net, decimal Tax)
{ public decimal Total => Net + Tax; }
public record CloseRefundRow(string Number, DateTime When, string Reason, string Method, decimal Amount);
/// <summary>One sold line feeding the Sales breakdowns — a POS till line (UserId = cashier) or an
/// online line attributed to this branch (UserId = null → shown as "Online orders").</summary>
public record SaleLine(string? UserId, string Product, string Category, int Quantity, decimal Gross, decimal DiscountAmount);

public class CloseSummaryVm
{
    public TillSession Session { get; set; } = null!;
    public bool Interim { get; set; }
    public string OpenedByName { get; set; } = "";
    public string ClosedByName { get; set; } = "";
    /// <summary>The staff at the till (current user for an open close; who closed it for a report) —
    /// shown in the window title, like EposNow.</summary>
    public string StaffName { get; set; } = "";
    public bool Closed => Session.ClosedAt != null;

    // Summary tab
    public int Transactions { get; set; }
    public List<TenderLine> Tenders { get; set; } = new();
    public decimal Takings => Tenders.Sum(t => t.Expected);
    public decimal Counted => Tenders.Sum(t => t.Counted ?? 0);
    public decimal Variance => Tenders.Sum(t => t.Variance);
    public decimal OpeningFloat => Session.OpeningFloat;

    // Float section
    public decimal Withdrawn { get; set; }
    public decimal Deposited { get; set; }
    public decimal ExpectedCashDrawer { get; set; }

    // Sales tab
    public int SalesQtyItems { get; set; }
    public decimal SalesDiscount { get; set; }
    public decimal SalesNet { get; set; }
    public decimal SalesTax { get; set; }
    public decimal SalesTotal => SalesNet + SalesTax;
    public decimal SalesAverage => Transactions > 0 ? SalesTotal / Transactions : 0;
    public List<SalesGroupRow> ByProduct { get; set; } = new();
    public List<SalesGroupRow> ByEmployee { get; set; } = new();
    public List<SalesGroupRow> ByCategory { get; set; } = new();

    // Other tabs
    public List<CloseRefundRow> Refunds { get; set; } = new();
    public List<VoidGroupRow> Voids { get; set; } = new();
    public List<TaxGroupRow> Taxes { get; set; } = new();
}

public interface ICashUpService
{
    /// <param name="currentUserId">Who is viewing — only used to label the window on an interim (open)
    /// close. Pass "" for a read-only report of a closed session.</param>
    Task<CloseSummaryVm> BuildAsync(TillSession session, bool interim, string currentUserId);
}

public class CashUpService : ICashUpService
{
    private readonly ApplicationDbContext _db;
    public CashUpService(ApplicationDbContext db) => _db = db;

    public async Task<CloseSummaryVm> BuildAsync(TillSession session, bool interim, string currentUserId)
    {
        var storeId = session.Register.StoreId;
        var winEnd = session.ClosedAt ?? DateTime.UtcNow;

        // POS sales rung up on this session, with their items + category for the breakdowns.
        var sales = await _db.Orders.Where(o => o.TillSessionId == session.Id)
            .Include(o => o.Items).ThenInclude(i => i.Product).ThenInclude(p => p.Category)
            .ToListAsync();
        var saleIds = sales.Select(s => s.Id).ToList();

        var payments = await _db.OrderPayments.Where(p => saleIds.Contains(p.OrderId))
            .Select(p => new { p.OrderId, p.Method, p.Amount }).ToListAsync();
        var withRows = payments.Select(p => p.OrderId).ToHashSet();
        decimal SumOf(string m) =>
            payments.Where(p => p.Method == m).Sum(p => p.Amount)
            + sales.Where(o => !withRows.Contains(o.Id) && o.PaymentProvider == m).Sum(o => o.Total);

        var giftCard = sales.Sum(o => o.GiftCardAmount);

        // ── Online orders: attribute ITEM revenue to the branch that supplied each item ───────────────
        // A branch earns the item-price of (a) items it fulfils from its OWN stock on orders it fulfils,
        // and (b) items it packs and SENDS to another branch to be merged ("Send to merge", counted when
        // dispatched). The fulfilling branch's own total excludes items transferred IN from other branches,
        // and DELIVERY FEES are never counted here (Logistics keeps those). So an order's item revenue is
        // split across the branches that actually supplied the goods, with no double-counting.
        int QtyOf(StockTransferItem ti) => ti.DispatchedQty ?? ti.ApprovedQty ?? ti.RequestedQty;
        var onlineLines = new List<SaleLine>();
        var onlineOrderIds = new HashSet<int>();

        // (b) Items THIS branch SENT to merge, dispatched within the window.
        var sentTransfers = await _db.StockTransfers
            .Where(t => t.FromStoreId == storeId && t.OrderId != null && t.DispatchedAt != null
                && t.DispatchedAt >= session.OpenedAt && t.DispatchedAt < winEnd
                && (t.Status == TransferStatus.InTransit || t.Status == TransferStatus.PartiallyReceived
                    || t.Status == TransferStatus.Completed))
            .Include(t => t.Items)
            .ToListAsync();
        var sentOrderIds = sentTransfers.Select(t => t.OrderId!.Value).Distinct().ToList();
        var sentOrders = sentOrderIds.Count == 0
            ? new List<Order>()
            : await _db.Orders.Where(o => sentOrderIds.Contains(o.Id))
                .Include(o => o.Items).ThenInclude(i => i.Product).ThenInclude(p => p.Category)
                .ToListAsync();
        var sentOrderById = sentOrders.ToDictionary(o => o.Id);
        foreach (var t in sentTransfers)
        {
            if (!sentOrderById.TryGetValue(t.OrderId!.Value, out var o)) continue;
            foreach (var ti in t.Items)
            {
                var oi = o.Items.FirstOrDefault(x => x.ProductId == ti.ProductId && x.ProductVariantId == ti.ProductVariantId);
                if (oi == null) continue;
                var qty = QtyOf(ti);
                if (qty <= 0) continue;
                var perUnitDisc = oi.Quantity > 0 ? oi.DiscountAmount / oi.Quantity : 0m;
                onlineLines.Add(new SaleLine(null, oi.ProductName,
                    oi.Product?.Category?.Name ?? "Uncategorised", qty, qty * oi.UnitPrice, qty * perUnitDisc));
                onlineOrderIds.Add(o.Id);
            }
        }

        // (a) Orders THIS branch fulfils (delivery or pickup), paid within the window — its OWN-stock items.
        var fulfilledOrders = await _db.Orders
            .Where(o => o.Channel == OrderChannel.Online && o.IsPaid
                && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Refunded
                && (o.FulfillingStoreId == storeId || o.PickupStoreId == storeId)
                && (o.PaidAt ?? o.CreatedAt) >= session.OpenedAt && (o.PaidAt ?? o.CreatedAt) < winEnd)
            .Include(o => o.Items).ThenInclude(i => i.Product).ThenInclude(p => p.Category)
            .ToListAsync();
        var fulfilledIds = fulfilledOrders.Select(o => o.Id).ToList();
        // Units that arrived FROM other branches for these orders — subtracted so only this branch's own
        // stock counts (the senders are credited for those via their own (b) above).
        var inbound = fulfilledIds.Count == 0
            ? new List<StockTransfer>()
            : await _db.StockTransfers.Where(t => t.ToStoreId == storeId && t.OrderId != null
                    && fulfilledIds.Contains(t.OrderId!.Value))
                .Include(t => t.Items).ToListAsync();
        foreach (var o in fulfilledOrders)
        {
            foreach (var oi in o.Items)
            {
                var transferredIn = inbound.Where(t => t.OrderId == o.Id).SelectMany(t => t.Items)
                    .Where(ti => ti.ProductId == oi.ProductId && ti.ProductVariantId == oi.ProductVariantId)
                    .Sum(QtyOf);
                var ownQty = oi.Quantity - transferredIn;
                if (ownQty <= 0) continue;
                var perUnitDisc = oi.Quantity > 0 ? oi.DiscountAmount / oi.Quantity : 0m;
                onlineLines.Add(new SaleLine(null, oi.ProductName,
                    oi.Product?.Category?.Name ?? "Uncategorised", ownQty, ownQty * oi.UnitPrice, ownQty * perUnitDisc));
                onlineOrderIds.Add(o.Id);
            }
        }

        // Website-orders tender = item-only online revenue attributed to this branch (delivery excluded).
        var website = onlineLines.Sum(l => l.Gross - l.DiscountAmount);

        // Counted-per-tender saved at close (null while the session is still open).
        Dictionary<string, decimal> counted = new();
        if (!interim && !string.IsNullOrWhiteSpace(session.CountedTenders))
        {
            try { counted = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, decimal>>(session.CountedTenders!) ?? new(); }
            catch { counted = new(); }
        }
        decimal? C(string k) => interim ? null : (counted.TryGetValue(k, out var v) ? v : 0);

        // Approved refunds for this session. A cash refund is money handed out of the drawer, so the Cash
        // tender's expected amount is net of cash refunds. (Card/transfer refunds don't touch the drawer.)
        var refundsAll = await _db.Refunds.Where(r => r.TillSessionId == session.Id && r.Status == RefundStatus.Approved)
            .Select(r => new { r.RefundNumber, r.CreatedAt, r.Reason, r.RefundMethod, r.Amount }).ToListAsync();
        var cashRefunds = refundsAll.Where(r => r.RefundMethod == "Cash").Sum(r => r.Amount);

        var tenders = new List<TenderLine>
        {
            new("Cash",     "Cash",          SumOf("Cash") - cashRefunds, C("Cash")),
            new("Card",     "Card",          SumOf("Card"),     C("Card")),
            new("Transfer", "Bank transfer", SumOf("Transfer"), C("Transfer")),
            new("GiftCard", "Gift card",     giftCard,          C("GiftCard")),
            new("Website",  "Website orders", website,          C("Website")),
        };

        // Cash drawer / float movements.
        var moves = await _db.CashMovements.Where(m => m.TillSessionId == session.Id)
            .Select(m => m.Amount).ToListAsync();
        var cashIn = moves.Where(a => a > 0).Sum();
        var cashOut = moves.Where(a => a < 0).Sum(a => -a);

        // Sales breakdowns (line-level; Net = gross − line discount, tax 0 for this catalogue). POS till
        // lines + the online item lines attributed to this branch above, so the Sales tab and item counts
        // reflect everything this branch sold — in-store AND its share of online orders.
        var posLines = sales.SelectMany(o => o.Items.Select(i => new SaleLine(
            o.UserId, i.ProductName,
            i.Product != null && i.Product.Category != null ? i.Product.Category.Name : "Uncategorised",
            i.Quantity, i.Quantity * i.UnitPrice, i.DiscountAmount))).ToList();
        var lines = posLines.Concat(onlineLines).ToList();

        var byProduct = lines.GroupBy(l => l.Product).Select(g => new SalesGroupRow(
                g.Key, g.Sum(x => x.Quantity), g.Sum(x => x.DiscountAmount),
                g.Sum(x => x.Gross - x.DiscountAmount), 0))
            .OrderByDescending(r => r.Total).ToList();
        var byCategory = lines.GroupBy(l => l.Category).Select(g => new SalesGroupRow(
                g.Key, g.Sum(x => x.Quantity), g.Sum(x => x.DiscountAmount),
                g.Sum(x => x.Gross - x.DiscountAmount), 0))
            .OrderByDescending(r => r.Total).ToList();

        var staffIds = sales.Select(o => o.UserId)
            .Append(session.OpenedByUserId).Append(session.ClosedByUserId ?? "").Append(currentUserId)
            .Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
        var staffNames = (await _db.Users.Where(u => staffIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FirstName, u.LastName, u.UserName }).ToListAsync())
            .ToDictionary(u => u.Id, u => { var n = $"{u.FirstName} {u.LastName}".Trim(); return string.IsNullOrWhiteSpace(n) ? (u.UserName ?? "—") : n; });
        var byEmployee = lines.GroupBy(l => l.UserId).Select(g => new SalesGroupRow(
                string.IsNullOrEmpty(g.Key) ? "Online orders" : staffNames.GetValueOrDefault(g.Key!, "—"),
                g.Sum(x => x.Quantity), g.Sum(x => x.DiscountAmount),
                g.Sum(x => x.Gross - x.DiscountAmount), 0))
            .OrderByDescending(r => r.Total).ToList();

        var totalNet = lines.Sum(l => l.Gross - l.DiscountAmount);

        return new CloseSummaryVm
        {
            Session = session,
            Interim = interim,
            OpenedByName = staffNames.GetValueOrDefault(session.OpenedByUserId, "—"),
            ClosedByName = string.IsNullOrEmpty(session.ClosedByUserId) ? "" : staffNames.GetValueOrDefault(session.ClosedByUserId!, "—"),
            StaffName = interim
                ? staffNames.GetValueOrDefault(currentUserId, "")
                : staffNames.GetValueOrDefault(session.ClosedByUserId ?? session.OpenedByUserId, ""),
            // In-store sales + the online orders this branch contributed items to.
            Transactions = sales.Count + onlineOrderIds.Count,
            Tenders = tenders,
            // Cash refunds are handed out of the drawer, so they count as cash withdrawn and reduce the
            // expected drawer. (All refunds are still shown on the Refunds tab + netted in revenue.)
            Withdrawn = cashOut + cashRefunds,
            Deposited = cashIn,
            ExpectedCashDrawer = session.OpeningFloat + SumOf("Cash") + cashIn - cashOut - cashRefunds,
            SalesQtyItems = lines.Sum(l => l.Quantity),
            SalesDiscount = lines.Sum(l => l.DiscountAmount),
            SalesNet = totalNet,
            SalesTax = 0,
            ByProduct = byProduct,
            ByEmployee = byEmployee,
            ByCategory = byCategory,
            Refunds = refundsAll.Select(r => new CloseRefundRow(r.RefundNumber, r.CreatedAt,
                string.IsNullOrWhiteSpace(r.Reason) ? "—" : r.Reason!, r.RefundMethod, r.Amount)).ToList(),
            Voids = new(),
            Taxes = lines.Any()
                ? new() { new TaxGroupRow("No Tax", 0m, lines.Sum(l => l.Quantity), totalNet, 0m) }
                : new(),
        };
    }
}
