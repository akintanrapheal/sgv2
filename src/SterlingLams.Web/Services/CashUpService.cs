using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

// ── EposNow-style "Sales & Operation" cash-up summary ──────────────────────────────────────────
// Shared by the POS close screen (PosController) and the read-only Inventory oversight view
// (Inventory/Till/Eod). Pure data build — no HTTP context — so it can run from either area.
/// <summary>One payment channel on the cash-up. <see cref="Countable"/> tenders (Cash, Card, Bank transfer)
/// are counted by the cashier at close and show an over/short; the rest (gift card, website) are recorded
/// automatically from the system and shown read-only.</summary>
public record TenderLine(string Key, string Label, decimal Expected, decimal? Counted, bool Countable = false)
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
    // Only counted (cash) tenders contribute to the counted total / over-short — the rest are auto-recorded.
    public decimal Counted => Tenders.Where(t => t.Countable).Sum(t => t.Counted ?? 0);
    public decimal Variance => Tenders.Where(t => t.Countable).Sum(t => t.Variance);
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

        // Website (online) orders are attributed to the store's designated "online till" ONLY — so they're
        // counted on exactly one session, not on every till open at the store (which double-counted them
        // and made the Z-reports never reconcile). A non-online till shows only the in-store sales rung up
        // on it. (If a store has no online till flagged, no session claims its website sales — they still
        // show in Finance, which attributes per order.)
        var isOnlineTill = session.Register?.HandlesOnlineOrders == true;

        // Website orders are dated by the day they were PACKED / marked ready (the day the branch did the
        // work) — the SAME rule Finance's End of Day uses (see EodDayRule), so the two EODs agree. They
        // belong to the Lagos DAY, not a single till shift — a branch may pack across sessions, before the
        // cashier opens the till AND after they close it. So the online till counts them across the WHOLE
        // business day, not just its open→close clock. The day is partitioned across the register's sessions
        // by OPEN time: this session owns online orders packed from its open — or the start of the day if it's
        // the day's first session — until the next session on this register opened (or the end of the day,
        // capped at now). That captures the early/late packs while never double-counting across two sessions in
        // one day. POS sales are tied to the session id, so they're unaffected.
        var onlineStart = session.OpenedAt;
        var onlineEnd = winEnd;
        if (isOnlineTill)
        {
            var dayStart = ReportCalendar.StartOfDayUtc(ReportCalendar.ToLocal(session.OpenedAt).Date);
            var dayEnd = dayStart.AddDays(1);
            var hasEarlier = await _db.TillSessions.AnyAsync(s => s.RegisterId == session.RegisterId
                && s.Id != session.Id && s.OpenedAt >= dayStart && s.OpenedAt < session.OpenedAt);
            onlineStart = hasEarlier ? session.OpenedAt : dayStart;
            var nextOpen = await _db.TillSessions
                .Where(s => s.RegisterId == session.RegisterId && s.Id != session.Id
                    && s.OpenedAt > session.OpenedAt && s.OpenedAt < dayEnd)
                .OrderBy(s => s.OpenedAt).Select(s => (DateTime?)s.OpenedAt).FirstOrDefaultAsync();
            var nowCapped = DateTime.UtcNow < dayEnd ? DateTime.UtcNow : dayEnd;
            onlineEnd = nextOpen ?? nowCapped;
        }

        // POS sales rung up on this session, with their items + category for the breakdowns. Voided sales
        // are excluded — a void returns the stock and removes the sale from takings.
        var sales = await _db.Orders.Where(o => o.TillSessionId == session.Id && o.VoidedAt == null)
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
        // A branch earns the item-price of every line it supplied (OrderItem.FulfilledFromStoreId), so a
        // split order's revenue lands on the branches that actually packed the goods, with no double-count.
        // DELIVERY FEES are never counted here (Logistics keeps those). See the attribution block below.
        var onlineLines = new List<SaleLine>();
        var onlineOrderIds = new HashSet<int>();

        // Website revenue is split into two buckets by how the order was paid: a bank-transfer order
        // (manually confirmed by staff with PaymentProvider "Transfer") is money to reconcile in the bank;
        // everything else is paid through the online gateway. (We don't take pay-on-delivery.)
        // "Transfer" (online gateway-less transfer) AND "Manual (Transfer)" (a staff-confirmed bank
        // transfer on a website order) both count as the bank-transfer bucket — not "paid online".
        static bool IsTransfer(Order o) => o.PaymentProvider != null
            && o.PaymentProvider.Contains("Transfer", StringComparison.OrdinalIgnoreCase);
        decimal wOnlineGross = 0m, wTransferGross = 0m;

        // A branch earns the merchandise value of every online line it SUPPLIED — i.e. the line's
        // OrderItem.FulfilledFromStoreId (set at fulfilment) is this store. Orders packed before that field
        // existed, or lines left unset, fall back to the order's pickup/fulfilling branch (its effective
        // supplier). Dated by the packed/ready time (the work event), matching Finance's EOD day rule, so a
        // website order shows in the EPOS cash-up on the day it was packed, not the day it was paid.
        //
        // Using the per-item field (not dispatched transfers) makes attribution independent of transfer
        // timing and of which branch merges/hands over the order: Allen is credited for an item it packed
        // even when Ikota is the pickup branch that merges it. Online-till only — one till per store claims
        // the online total, so a multi-till store doesn't count the same website sale twice.
        var onlineOrders = !isOnlineTill ? new List<Order>() : await _db.Orders
            .Where(o => o.Channel == OrderChannel.Online && o.IsPaid && o.VoidedAt == null
                && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Refunded
                && (o.FulfillingStoreId == storeId || o.PickupStoreId == storeId
                    || o.Items.Any(i => i.FulfilledFromStoreId == storeId))
                && (o.PackedAt ?? o.PickupReadyEmailedAt) != null
                && (o.PackedAt ?? o.PickupReadyEmailedAt) >= onlineStart
                && (o.PackedAt ?? o.PickupReadyEmailedAt) < onlineEnd)
            .Include(o => o.Items).ThenInclude(i => i.Product).ThenInclude(p => p.Category)
            .ToListAsync();
        foreach (var o in onlineOrders)
        {
            var fallbackSid = o.PickupStoreId ?? o.FulfillingStoreId;
            foreach (var oi in o.Items)
            {
                var supplier = oi.FulfilledFromStoreId ?? fallbackSid;   // null line → effective supplier
                if (supplier != storeId) continue;                      // this branch didn't supply this line
                if (oi.Quantity <= 0) continue;
                onlineLines.Add(new SaleLine(null, oi.ProductName,
                    oi.Product?.Category?.Name ?? "Uncategorised", oi.Quantity, oi.Quantity * oi.UnitPrice, oi.DiscountAmount));
                onlineOrderIds.Add(o.Id);
                var net = oi.Quantity * oi.UnitPrice - oi.DiscountAmount;
                if (IsTransfer(o)) wTransferGross += net; else wOnlineGross += net;
            }
        }

        // Counted-per-tender saved at close (null while the session is still open). Only cash is counted.
        Dictionary<string, decimal> counted = new();
        if (!interim && !string.IsNullOrWhiteSpace(session.CountedTenders))
        {
            try { counted = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, decimal>>(session.CountedTenders!) ?? new(); }
            catch { counted = new(); }
        }
        decimal? C(string k) => interim ? null : (counted.TryGetValue(k, out var v) ? v : 0);

        // Approved POS refunds for this session. Each refund is netted against the SAME channel it was paid
        // back through (a cash refund reduces cash, a card refund reduces card, a transfer refund reduces
        // transfer) — so every channel's figure is after its own refunds.
        var refundsAll = await _db.Refunds.Where(r => r.TillSessionId == session.Id && r.Status == RefundStatus.Approved)
            .Select(r => new { r.RefundNumber, r.CreatedAt, r.Reason, r.RefundMethod, r.Amount }).ToListAsync();
        var cashRefunds     = refundsAll.Where(r => r.RefundMethod == "Cash").Sum(r => r.Amount);
        var cardRefunds     = refundsAll.Where(r => r.RefundMethod == "Card").Sum(r => r.Amount);
        var transferRefunds = refundsAll.Where(r => r.RefundMethod == "Transfer").Sum(r => r.Amount);

        // Website refunds: approved refunds on ONLINE orders, approved within this window, reduce THIS
        // branch's website figure by the value of the refunded items this branch supplied (mirroring how
        // website SALES are split across branches), netted into the matching bucket (online vs transfer).
        decimal wOnlineRefund = 0m, wTransferRefund = 0m;
        // Website refunds attributed to THIS branch also get listed on the Refunds tab (not just netted
        // into the figures) so the cash-up shows every refund that reduced this branch's takings.
        var websiteRefundRows = new List<CloseRefundRow>();
        var onlineRefunds = !isOnlineTill ? new List<Refund>() : await _db.Refunds
            .Where(r => r.Status == RefundStatus.Approved && r.TillSessionId == null
                && r.DecisionAt != null && r.DecisionAt >= onlineStart && r.DecisionAt < onlineEnd)
            .Include(r => r.Items)
            .Include(r => r.OriginalOrder).ThenInclude(o => o.Items)
            .ToListAsync();
        foreach (var r in onlineRefunds)
        {
            var o = r.OriginalOrder;
            if (o == null || o.Channel != OrderChannel.Online) continue;
            var fallbackSid = o.PickupStoreId ?? o.FulfillingStoreId;
            decimal branchRefund = 0m;
            foreach (var ri in r.Items)
            {
                var oi = o.Items.FirstOrDefault(x => x.ProductId == ri.ProductId && x.ProductVariantId == ri.ProductVariantId);
                var supplier = oi?.FulfilledFromStoreId ?? fallbackSid;    // which branch supplied the refunded line
                if (supplier != storeId) continue;                        // not this branch's line — don't net it here
                if (ri.Quantity <= 0) continue;
                var refundedValue = ri.Quantity * ri.UnitPrice;
                if (IsTransfer(o)) wTransferRefund += refundedValue; else wOnlineRefund += refundedValue;
                branchRefund += refundedValue;
            }
            if (branchRefund > 0)
                websiteRefundRows.Add(new CloseRefundRow(r.RefundNumber, r.DecisionAt ?? r.CreatedAt,
                    string.IsNullOrWhiteSpace(r.Reason) ? $"Website order {o.OrderNumber}" : r.Reason!,
                    string.IsNullOrWhiteSpace(r.RefundMethod) ? "Website" : $"Website · {r.RefundMethod}",
                    Math.Round(branchRefund, 2)));
        }

        // A sales channel must never read NEGATIVE: that happens when a channel's refunds exceed its sales
        // this session (typically refunding a website order sold on an earlier day). Floor each auto-recorded
        // channel at 0 and carry the excess into one explicit "Refunds (earlier sales)" line, so no sales
        // row goes negative and the Takings total is unchanged. (Cash is left netted — the drawer section
        // already shows cash refunds via "Cash withdrawn / refunds".)
        decimal overflow = 0m;
        decimal ClampNet(decimal net) { if (net < 0) { overflow += net; return 0m; } return net; }

        var tenders = new List<TenderLine>
        {
            // Cash, Card and Bank transfer are COUNTED at close (the cashier confirms the drawer, the card
            // terminal total and the transfers received) so each shows an over/short. The two website
            // buckets are also countable so the cashier can reconcile them against Paystack / the bank
            // (confirm the online takings landed) and see an over/short. Gift card stays auto-recorded.
            new("Cash",            "Cash",                    SumOf("Cash")     - cashRefunds,     C("Cash"),     Countable: true),
            new("Card",            "Card",                    ClampNet(SumOf("Card")     - cardRefunds),     C("Card"),     Countable: true),
            new("Transfer",        "Bank transfer",           ClampNet(SumOf("Transfer") - transferRefunds), C("Transfer"), Countable: true),
            new("GiftCard",        "Gift card",               giftCard,                            null),
            new("WebsiteOnline",   "Website – paid online",   ClampNet(wOnlineGross   - wOnlineRefund),   C("WebsiteOnline"),   Countable: true),
            new("WebsiteTransfer", "Website – bank transfer", ClampNet(wTransferGross - wTransferRefund), C("WebsiteTransfer"), Countable: true),
        };
        if (overflow < 0)
            tenders.Add(new("RefundsBeyondSales", "Refunds (earlier sales)", overflow, null));

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
            // POS refunds on this session + website refunds attributed to this branch, newest first.
            Refunds = refundsAll.Select(r => new CloseRefundRow(r.RefundNumber, r.CreatedAt,
                    string.IsNullOrWhiteSpace(r.Reason) ? "—" : r.Reason!, r.RefundMethod, r.Amount))
                .Concat(websiteRefundRows)
                .OrderByDescending(r => r.When).ToList(),
            Voids = new(),
            Taxes = lines.Any()
                ? new() { new TaxGroupRow("No Tax", 0m, lines.Sum(l => l.Quantity), totalNet, 0m) }
                : new(),
        };
    }
}
