# Inventory System — Review & Roadmap

Tab-by-tab review of the Inventory System (`/Inventory` area) with advanced-feature suggestions.
Grounded in the current code (controllers/views/models). Status legend: ✅ built · ◐ partial · ➕ proposed.

_Last reviewed: 2026-06-23. Progress updated: 2026-06-24._

---

## Status — shipped / deferred (2026-06-24)

**✅ Shipped this cycle**
- **Dead-stock & aging report** — `/Inventory/Reports/DeadStock` (no sale in 30/60/90/180/365 days,
  category filter, units + capital tied up at retail, CSV).
- **Overview date-range + branch filter** — window (7/30/90) + branch scope across KPIs, sales,
  trend chart, per-branch and recent movements.
- **Stock-alert digest** — `LowStockAlertService` now groups per (product, branch) and splits
  **Negative (oversold) / Out of stock / Low** into per-branch email sections (settings-gated).
- **Variant-level stock in the POS picker** — the variant chooser shows per-variant available
  stock, disables sold-out options, caps qty per variant (schema/service for variant stock already
  existed; this surfaced it). See [POS_ROADMAP.md](POS_ROADMAP.md).

**⏸ Deferred by owner decision**
- **Cost & margin (COGS)** and **Purchasing (Supplier / PO / GRN)** — explicitly out of scope for
  now. Valuation stays at retail. These remain the highest-value *big bets* if revisited.

---

## Overall verdict

A genuinely capable multi-branch inventory + POS: real stock ledger (`StockMovement`),
confirmation-based inter-branch transfers, stock-take, adjustments with reason→movement mapping,
offline-capable POS, and a deep reports suite. **The biggest missing layer is the inbound/financial
side** — there is **no supplier, no purchasing/PO/goods-receipt, and no unit cost**, so there's no
true cost valuation, no margin/profit, and reorder is threshold-only. That's where the
highest-value work is.

---

## Tab-by-tab

### Overview ✅
KPIs (SKUs, out/low stock, units on hand, POS today), charts (sales trend, units-by-branch, stock
health), top products/staff, recent movements, alerts.
- ✅ Date-range + branch filter (shipped).
- ➕ Retail KPIs: **stock turnover, days-of-cover, sell-through, GMROI, dead-stock value**.

### Point of Sale — Sessions / Registers / Discount reasons / POS settings ✅
Till sessions, register CRUD, discount reasons + presets, POS settings.
- ➕ Session/Z-report analytics (cash-variance trends per cashier), enforce **blind cash-up**,
  **end-of-day auto-close**, cashier-performance view.

### Sales — Completed / Outstanding / Saved ✅
Completed + outstanding lists with filters, parked carts (Saved), order detail.
- ➕ **Layaway/installments** (big in jewellery), partial payments, **returns/RMA** distinct from
  refunds, **trade-in/buy-back**.

### CRM — Customers / Discounts ✅
Customer list + detail, discount codes.
- ➕ **RFM segments / customer lifetime value**, loyalty-tier view, ring-size/preferences profile,
  birthday/anniversary reminders, targeted offers.

### Inventory (core)
- **All items** ✅ — catalog CRUD, variants, labels, per-item history, lookup.
- **Categories** ◐ — basic list. ➕ per-category stock/margin rollups.
- **Stock levels** ✅ — grid + scan + bulk Set-all + CSV.
- **Stock adjustment** ✅ — multi-line, reason codes (Received/Damage/Loss/Correction), expiry dates,
  product search. ◐ "Received" has **no supplier/PO link**.
- **Stock-take** ◐ — scan + apply with variance. ➕ scheduled/cycle counts (ABC), freeze during
  count, variance approval, two-person verify.
- **Stock transfer** ✅ — full request→approve→dispatch→receive→complete + receipt.
- **Stock history** ✅ — movement ledger.

### Reports ✅ (extensive)
Reorder, Stock value, Movements, Shrinkage, Sales summary/by item/category/customer/staff, Payments,
Discounts, Expiring, Valuation (charts added to several).
- ◐ **Valuation is at RETAIL price, not cost** (no cost field); **no profit/margin report**.
- ➕ **ABC analysis, dead/slow-mover, stock-turnover, sales-velocity & forecast**.

### Administration — Staff & Roles / Branches / Activity Log ✅
Cashiers + PINs + store assignment, branches, audit log.
- ➕ **Per-branch + per-action permissions**, approval roles, segregation-of-duties on
  adjustments/transfers.

---

## Advanced features (ranked)

### 🔝 Big bets (highest value)
1. **Purchasing module** — `Supplier` + `PurchaseOrder` (draft→sent→received) + **Goods Receipt
   (GRN)** posting `Purchase` movements. Unlocks the **"On order"** column (currently always "—"),
   supplier price lists, and **Reorder report → one-click draft PO**.
2. **Cost & margin (COGS)** — add **unit cost** (moving-average or last cost) → **valuation at cost**,
   **profit/margin** on products + sales reports, GMROI. Foundational for real inventory accounting.
3. **Jewellery: metal-price-linked pricing & valuation** — products already store **Metal, Carat,
   Weight, Gemstone**; hook a **gold/silver price feed** to auto-reprice and value by weight/purity.
4. **Per-piece serial tracking + certificates** — individual high-value pieces with diamond/gold
   **certificate attachments** and full provenance/lifecycle.
5. **Demand-based auto-reorder** — reorder point + reorder qty + lead time per product, driven by
   **sales velocity** → suggested/auto-draft POs and **inter-branch rebalancing suggestions**.

### ⚡ Quick wins
- ✅ **Dead-stock & aging report** (no sales in N days, value tied up) — **shipped**.
- ✅ **Stock-alert digest** — extended `LowStockAlertService` to a per-branch low/out/negative
  email digest — **shipped**.
- ✅ **Overview date-range + branch filters** — **shipped**.
- ➕ **Per-item movement timeline** on the product page (receipts/sales/transfers/counts/adjustments).
- ➕ **Reorder point + reorder qty** fields per product (even before full purchasing).
- ➕ **Bulk product/price import** (CSV) + **bulk label printing** by receipt/PO.

### 🛠 Medium
- **Cycle counting (ABC)** + variance approval + count freeze.
- **Supplier returns (RTV)** and proper **customer returns/RMA**.
- **Repairs/resizing service tickets** (jewellery), **layaway**.
- **Approval thresholds** on adjustments/write-offs.

---

## Suggested sequencing

1. **Cost + margin layer** (#2) — unblocks true valuation & profit reporting.
2. **Purchasing / PO / GRN** (#1) — pairs naturally with cost; enables "On order" + reorder→PO.
3. **Reorder intelligence** (#5) + dead-stock/turnover reports.
4. **Jewellery specifics** (#3 metal-price pricing, #4 serial+certificates).
5. Quick wins folded in throughout.

## Notes / current-code references
- Stock ledger: `StockMovement` (+ `AdjustmentReasons` maps reasons → movement types).
- No `Supplier`/`PurchaseOrder` domain models; `Product` has no `Cost`/reorder-point/lead-time fields.
- Existing background services to extend: `LowStockAlertService`, `BackInStockNotifier`.
- Valuation/Stock-value reports compute `qty × Product.Price` (retail), not cost.
