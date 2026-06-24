# POS (Till) — Review & Roadmap

Review of the in-store POS (`/Pos` controller + `Views/Pos/Sell.cshtml` + the offline PWA) with
advanced-feature suggestions. Grounded in the current code. Status legend: ✅ built · ◐ partial · ➕ proposed.

_Last reviewed: 2026-06-23. Progress updated: 2026-06-24._

---

## Overall verdict

A mature, offline-capable retail POS: register binding → PIN login (rate-limited) → cash float /
open session → EPOS-style category+product grid with scan, variant picker, per-line notes &
configurable discounts → **mandatory customer attach** → checkout with change → loyalty accrual →
barcode receipt. Plus hold/recall parked sales, partial refunds (row-locked, loyalty claw-back),
cross-branch stock lookup, QR store-pickup verification, cash-up → Z-report → till oversight, and a
real offline PWA (IndexedDB snapshot, idempotent offline-sale sync, best-effort oversell flagging).
Concurrency is handled carefully throughout.

---

## Status — shipped / deferred (2026-06-24)

**✅ Shipped this cycle (the quick-wins batch)**
- **X-report** — mid-shift cash/sales read without closing the session (`/Pos/Xreport`); shared
  Z-report view, interim mode hides counted/variance.
- **Cash in / out** — `CashMovement` table records signed pay-ins / pay-outs against the shift
  (menu modal); folded into the Z/X-report expected-cash and the manager's till oversight.
- **Email receipt** — `/Pos/EmailReceipt` sends a branded receipt to the buyer (sale-complete modal
  + standalone receipt page); back-fills a missing email onto the customer.
- **Split / mixed payment** — `OrderPayment` table; settle one sale across cash/card/transfer
  (change taken from cash), "Split" modal with live remaining/change; receipt + Z-report split-aware.
  (Blocked offline — the offline queue is single-method.)
- **Quick / miscellaneous item** — one-off / un-barcoded line with its own name + price, no stock,
  via a hidden lazily-created "Custom item" product. (Blocked offline.)
- **Per-variant stock in the variant picker** — shows "{n} in stock" / "Out of stock" per option,
  disables sold-out variants, caps qty per variant (server already enforced it).

**Storefront tidy-up (related):** removed the redundant "+₦X for this option" variant note on the
product page — the headline price already updates to the chosen variant's full price.

---

## Tab / area-by-area

**Login / register / sessions** ✅ — PIN login, rate-limited, lockout-aware; admin-PIN-gated register
change; float open + cash-up.
- ➕ Per-cashier accountability within a shared session; **end-of-day auto-close**.

**Catalog / scan** ✅ — name/SKU/barcode search, EPOS grid, variant picker (now stock-aware),
product popup, hide-from-POS.
- ➕ Favourites / quick-keys panel.

**Cart / checkout** ✅ — per-line notes + configurable discounts, mandatory customer, split payment.
- ➕ **Cart-level discount** + **manager-PIN price override**; **tax/VAT line** (Order.Tax exists but
  is unused — prices are VAT-inclusive today).

**Refunds** ✅ — partial, row-locked, loyalty claw-back, audited.
- ➕ **Exchange** (refund + rebuy in one transaction); refund-method policy guard.

**Cash-up / Z-report** ✅ — counted vs expected with variance; X-report; cash in/out folded in.
- ➕ **Z-report auto-email** to the branch manager on close; **per-cashier breakdown**.

**Receipts** ✅ — printable, barcode, branding; email receipt.
- ➕ **Gift receipt** (price-hidden).

**Offline / PWA** ✅ — snapshot + 30-min `setInterval` + sync-on-reconnect; offline receipt.
- ➕ **Background Sync API** so a *closed* PWA still drains its queue.

---

## Advanced features (ranked) — remaining

### ⚡ Quick wins
_All five original quick wins shipped (see above)._

### 🛠 Medium
- **Exchange flow** (refund-and-rebuy, netting the difference).
- **Cart-level discount + manager-PIN price override** (reuse the admin-PIN gate from ChangeRegister).
- **Gift receipt** toggle.
- **Z-report auto-email** to branch manager on session close (settings-gated, like the stock digest).
- **Per-cashier sales attribution** on the Z-report.

### 🔝 Bigger bets
- **Background Sync API** for offline queue drain when the PWA is closed.
- **Layaway / deposit-and-collect** — partial-payment plans over time (jewellery-relevant; builds on
  parked sales + a balance ledger).
- **Cash-drawer & thermal-printer hardware** integration (WebUSB/serial or a print-agent).
- **Barcode/label print station** tied to the inventory barcode work.

---

## Notes / current-code references
- `Controllers/PosController.cs` — Checkout (split-payment + custom-item aware), SyncSales /
  IngestOfflineSaleAsync (offline), Xreport, CashInOut/CashMovements, EmailReceipt, Search/Snapshot
  (per-variant stock), StockLookup, PickupVerify/Complete.
- Tables added this cycle: `CashMovement`, `OrderPayment`; `Product.IsCustomItem` flag.
- Offline: `wwwroot/js/pos-offline.js` (snapshot + fetch shim + offline sale queue + 30-min sync),
  `wwwroot/pos-sw.js`. Split & quick-item are intentionally online-only.
- Variant stock: `StoreInventory.ProductVariantId` (+ pool fallback) and
  `StockService.GetAvailableAsync(productId, variantId, storeId)`.
