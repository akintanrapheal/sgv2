# Admin Console — Review & Roadmap

Section-by-section review of the website Admin backend (`/Admin` area) with advanced-feature
suggestions. Grounded in the current code. Status legend: ✅ built · ◐ partial · ➕ proposed.

_Last reviewed: 2026-06-23. Progress updated: 2026-06-24._

---

## Overall verdict

A mature, well-structured admin: section-based RBAC (roles → grantable sections, store-level
scoping, audit logging on mutations), charts wired, CSV exports on most lists. The whole review's
backlog — quick wins, mediums, and all three big bets — has now shipped.

---

## Status — shipped / deferred (2026-06-24)

**✅ Shipped — quick wins**
- **Dashboard period-over-period deltas** — ▲/▼ % vs prior period on KPI cards (today vs yesterday,
  MTD vs last-month-MTD) via a `_KpiDelta` partial.
- **AOV + online/POS channel split** on the dashboard.
- **Low-stock uses `LowStockThreshold`** (floored at 1) instead of a hardcoded `< 3`.
- **Orders filters** — channel, payment-status, date-range; search now matches phone; filters carry
  through tabs, pagination and CSV export.
- **Resend email on an order** — order summary, or QR pickup pass for store-pickup orders (audited).

**✅ Shipped — mediums**
- **Orders "Needs action" tab** — paid orders still awaiting staff (Pending/Confirmed/Processing/
  AwaitingTransfer) with its own count.
- **Packing slip + invoice** — printable from the order detail (`PrintDoc` view; invoice has prices/
  totals/paid badge, packing slip has items + ship-to, no prices).
- **Customer loyalty panel** — balance + ledger on the detail; manual credit/debit (audited, never
  below zero) via `LoyaltyService.AdjustAsync`.
- **Customer segmentation + tags** — VIP / Repeat / Lapsed / New badges + free-text tags
  (`ApplicationUser.Tags`); list filters by segment and tag.
- **Marketing section** — surfaces abandoned carts + back-in-stock requests (already collected by the
  storefront) + a dashboard tile with open counts.

**✅ Shipped — big bets**
- **Reports consolidation** — _reverted by owner preference._ Admin keeps its **own** Reports
  (Sales / Best Sellers / Stock) and the Inventory System keeps its full suite; both areas have
  reports. (The earlier consolidation that redirected Admin → Reports into Inventory was undone.)
- **Global search** — top-bar box → `/Admin/Search`, grouped results (orders / customers / products);
  permission-aware (each group only shown if the user can access that section).
- **Staff 2FA** — authenticator-app (TOTP) two-factor: login challenge (code or recovery code,
  "trust this device"), Account → Security to enable (QR/manual-key, verify-before-on), recovery
  codes, regenerate, disable; linked from the admin top bar. Uses Identity's token store (no
  migration). Enable/disable audited.

**Also:** product edit now **keeps your scroll position** after variant/image section saves.

**⏸ Deferred / not done**
- **Mandatory 2FA enforcement** for staff roles — 2FA is currently **opt-in per user**. Forcing
  un-enrolled staff to set it up before reaching the backend is a policy toggle, offered but not built.

---

## Section-by-section

**Dashboard** ✅ — KPIs + deltas, AOV, channel split, revenue chart (7/30/90), top products,
orders-by-status, recent orders, low-stock (threshold-based), marketing tile.

**Orders** ✅ — status tabs + "Needs action", filters (channel/payment/date/phone), detail, status +
bulk update, notes, refund, tracking, resend email, packing slip + invoice, CSV.
- ➕ Saved views / smart segments.

**Customers** ✅ — list with segment badges + tags + filters, detail with loyalty panel, CSV.
- ➕ Manual "add customer"; merge duplicates; marketing-consent flag.

**Marketing** ✅ — abandoned carts + back-in-stock lists (read-only) + dashboard tile.
- ➕ One-click "send reminder now"; recovery-rate metrics.

**Products / Categories / Attributes / Stores / Discounts / Users / Roles / Audit Log / Emails /
Settings** ✅ — full CRUD; RBAC with store-level scoping; audit; email customizer; DB-driven settings.
- ➕ Bulk price/category edit surfaced; SEO fields.

**Reports** ✅ — Admin's own Sales / Best Sellers / Stock reports (the Inventory System also has its
full suite).

**Search** ✅ — global, permission-aware.

**Security (Account)** ✅ — change password, **2FA** (TOTP).

---

## Advanced features (ranked) — remaining

### 🛠 Medium
- **One-click abandoned-cart reminder** + recovery-rate reporting on the Marketing page.
- **Manual add-customer** + duplicate merge.
- **Saved order views** (smart segments beyond the status tabs).

### 🔝 Bigger bets
- **Mandatory 2FA enforcement** for staff roles (gate the backend until enrolled).
- **WebAuthn / passkeys** as a second-factor option alongside TOTP.

---

## Notes / current-code references
- RBAC: `Areas/Admin/AdminSections.cs` (+ new **Marketing** section), `Services/PermissionService.cs`,
  `Infrastructure/RoleSeedData.cs` (default grants — none include "Reports").
- New this cycle: `MarketingController`, `SearchController`; `_KpiDelta` partial; `PrintDoc` view;
  2FA in `Controllers/AccountController.cs` (+ `TwoFactorLogin`/`Security`/`SetupTwoFactor` views).
- Tables/columns: `CustomerTags` (`ApplicationUser.Tags`). 2FA uses Identity's existing token store.
- Loyalty: `LoyaltyService.AdjustAsync` (manual, audited, clamps ≥ 0).
