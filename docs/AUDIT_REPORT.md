# Sterling Lams Platform — Full Audit Report (Findings)

**Date:** 2026-06-12
**Scope:** Entire ASP.NET Core MVC + EF Core (PostgreSQL) application — customer storefront, Areas/Admin, Areas/Inventory (POS/inventory system), database schema, security/permissions, and performance.
**Method:** Codebase-grounded read-only audit via 5 parallel research passes (Database & Domain, Inventory/POS Concurrency, Security & Permissions, Customer Storefront, Admin Backend & Reporting/Performance). No code changed yet — this is Stage 1 (audit). Stage 2 is a phased fix plan, prioritized with the user.

---

## Production Readiness Scorecard (current state, before fixes)

| Dimension | Score /10 | Why |
|---|---|---|
| Technical Debt | 6 | Solid architecture & recently-built transfer workflow are good; Admin vs Inventory area duplication and some sequential-loop patterns need cleanup. |
| Scalability | 4 | Several reports/exports do in-memory aggregation and unbounded loads — will not survive hundreds of thousands of rows/month. |
| Security | 3 | Hardcoded payment secret key in committed config, path-traversal file upload, weak cookie policy, and no store-level authorization are critical/high issues. |
| Inventory Integrity | 4 | Ledger design (StockMovement, StockReservation, Transfer workflow) is sound, but check-then-act races in POS checkout and reservations can cause overselling, and `StoreInventory` has no concurrency token. |
| SEO | 6 | JSON-LD product schema, sitemap, canonical/OG tags exist; missing review/rating schema and image sitemap entries. |
| Conversion/UX | 5 | Good base (cart, wishlist, checkout); missing reviews, recommendations, abandoned-cart recovery, back-in-stock fulfillment, image dimensions causing CLS. |

**Overall: ~4.5/10** — a well-structured foundation, but **not yet safe at the stated target volume** (hundreds of thousands of transactions/month across branches) without addressing the Critical items below, most importantly the POS/reservation race conditions and the committed secret key.

---

## CRITICAL Findings

### C1. Hardcoded live-format Paystack secret key committed to source
- **File:** `appsettings.Development.json:16-17`
- **Issue:** `Paystack:SecretKey` / `PublicKey` are real test keys checked into git.
- **Impact:** If the repo is ever shared/breached, attacker can hit the Paystack API as this merchant (refunds, transaction listing). Must rotate and move to user-secrets/environment variables/Key Vault.

### C2. Path traversal in admin file upload
- **File:** `Areas/Admin/Controllers/UploadController.cs:29`
- **Issue:** `subfolder` is only `Trim('/')`'d, then `Path.Combine(WebRootPath, "uploads/" + subfolder)` — a value like `../../somewhere` escapes the uploads directory.
- **Impact:** Authenticated admin user (or anyone reaching this endpoint) could write files outside `wwwroot/uploads`, up to webshell/RCE risk depending on hosting.

### C3. POS checkout — stock availability check happens outside the DB transaction (overselling)
- **File:** `Controllers/TillController.cs:663-717`
- **Issue:** `_stock.GetStockAsync(...)` is checked *before* `BeginTransactionAsync()`. Two concurrent checkouts can both pass the check on the same low-stock item, then both decrement, producing negative on-hand stock.
- **Impact:** For high-value, low-quantity jewelry SKUs, this is the single highest-risk bug for the multi-branch POS rollout — double-sold items, refunds, customer-trust damage.

### C4. Online order reservation — same check-then-act race
- **File:** `Services/OrderFulfilmentService.cs:99-119`
- **Issue:** Available quantity is computed from an in-memory snapshot loaded *before* the transaction; two concurrent checkouts can both reserve the same units, pushing `QuantityReserved > QuantityOnHand`.
- **Impact:** Oversold web orders that can't be fulfilled; manual reconciliation burden.

### C5. `StoreInventory` has no optimistic concurrency token
- **File:** `Models/Domain/StoreInventory.cs`
- **Issue:** No `RowVersion`/`xmin` concurrency token, unlike `ApplicationUser`. Even inside a transaction, EF won't detect a concurrent overwrite — last write wins.
- **Impact:** Underlies C3/C4 — without a token, even adding `SELECT ... FOR UPDATE`-style protections is harder to verify; concurrent POS sale + transfer dispatch on the same product/store can silently lose an update.

### C6. Cookie security policy is `SameAsRequest`, not `Always`
- **File:** `Program.cs:62`
- **Issue:** `options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest`. If the app is ever reached over plain HTTP (misconfigured proxy), auth cookies go out without `Secure`.
- **Impact:** Session-hijacking risk for staff/admin accounts handling customer PII and inventory.

---

## HIGH Findings

### H1. Order money columns missing `HasPrecision(18,2)`
- **File:** `Models/Domain/Order.cs:47-48` (`AmountTendered`, `ChangeGiven`)
- **Issue:** Unlike other money columns, these lack explicit decimal precision — Npgsql may map to `numeric` with default/variable scale, risking rounding drift in cash-up reconciliation.

### H2. `StockReservation.ProductId` / `StoreId` have no FK constraints or compound index
- **File:** `Models/Domain/StockReservation.cs`, `Migrations/20260610164553_StockReservations.cs:15-35,38-41`
- **Issue:** Only `OrderId` is a real FK; `ProductId`/`StoreId` are bare ints. No `(ProductId, StoreId)` index, so reservation lookups during checkout/transfer scan the table.
- **Impact:** Orphaned reservations possible if a product/store is deleted; slow reservation lookups at scale.

### H3. Transfer approval reserves stock without re-checking for concurrent approvals
- **File:** `Services/TransferWorkflowService.cs:103-155` (load at 125-127, blind `+=` at 145)
- **Impact:** Two transfers approved back-to-back for the same product can over-reserve `FromStore` stock, requiring manual reject/cancel cleanup. Same root cause as C5.

### H4. Transfer dispatch decrements `QuantityReserved` via blind `Math.Max(0, ...)` without reload
- **File:** `Services/TransferWorkflowService.cs:212`
- **Impact:** Concurrent dispatch + a second approval on the same product can under-count reserved stock.

### H5. `ReleaseReservationAsync` / `ReleaseRowsAsync` not wrapped in its own transaction
- **File:** `Services/OrderFulfilmentService.cs:123-143`
- **Impact:** Partial failure can leave `QuantityReserved` decremented but the `StockReservation` row not deleted (or vice versa) — reserved stock can "leak" and block new orders.

### H6. Refund flow re-reads "already refunded" qty once, before the loop (double-refund race)
- **File:** `Controllers/TillController.cs:210-215, 242`
- **Impact:** Two concurrent refund requests for the same order line can both pass the "not yet fully refunded" check and double-refund the same item — stock ledger over-credits and till cash-up won't reconcile.

### H7. No store-level authorization on Inventory transfers/stock
- **File:** `Areas/Inventory/Controllers/TransfersController.cs:185-189`, `Areas/Inventory/Controllers/StockController.cs`
- **Issue:** Any Inventory-role user can view/act on any branch's transfer or adjust any branch's stock by ID — no check that the user is assigned to that store.
- **Impact:** A compromised or rogue branch account can manipulate another branch's stock/transfers undetected. (Note: per project context this may be **intentional** for now — single-tenant, all Inventory users trusted across branches — but flagged because the requested granular permission system implies otherwise.)

### H8. Weak password policy + no email confirmation requirement
- **File:** `Program.cs:43-46`
- **Issue:** `RequireUppercase = false`, `RequireConfirmedEmail = false`.
- **Impact:** Weaker staff/admin account security; customers can use unverified emails for accounts/orders.

### H9. 30-day sliding auth cookie for all users including staff/admin
- **File:** `Program.cs:63`
- **Impact:** Long window for session theft; slow to revoke departed staff access.

### H10. Reports do client-side/in-memory aggregation over all products × stores
- **File:** `Areas/Inventory/Controllers/ReportsController.cs:26-37, 70-99`
- **Impact:** O(products × stores) memory blow-up; will be visibly slow well before "hundreds of thousands of transactions/month" scale (10k SKUs × 5 stores = 50k+ in-memory tuples per report load).

### H11. Audit log export has no row limit
- **File:** `Areas/Admin/Controllers/AuditLogController.cs:59-64`
- **Impact:** At expected volumes (~500k audit rows/month), unfiltered export will OOM or hang the request.

### H12. Audit log filter dropdowns run `SELECT DISTINCT` over the entire table on every page load
- **File:** `Areas/Admin/Controllers/AuditLogController.cs:45-46`
- **Impact:** Linear slowdown of the audit page as the table grows into millions of rows.

### H13. Stock bulk-update and Stocktake "apply" loop one DB query per line item
- **File:** `Areas/Inventory/Controllers/StockController.cs:134-147`, `Areas/Inventory/Controllers/StocktakeController.cs:96-107`
- **Impact:** A 100-line stock count/adjustment batch = 200+ sequential round trips; risk of request timeout on big stocktakes.

### H14. Customers list/export does per-row `Orders.Count`/`Sum` (N+1)
- **File:** `Areas/Admin/Controllers/CustomersController.cs:47-52,118-119`
- **Impact:** 30 customers/page = 60 extra queries; export over all customers will be very slow at scale.

### H15. Audit log entries don't capture old/new values
- **File:** `Services/AuditService.cs:24-42`
- **Impact:** Can't answer "what was the value before this change" for inventory adjustments, refunds, role changes — undermines the "complete audit trail" requirement.

### H16. Storefront: no product reviews/ratings (model, controller, or schema)
- **File:** `Views/Products/Detail.cshtml:330-356` (hardcoded "Reviews (0)"), `Models/Domain/Product.cs`
- **Impact:** No social proof on jewelry PDPs (high-AOV category where reviews matter most for conversion); JSON-LD `Product` schema can't include `aggregateRating`, losing star-rating rich results in search.

### H17. Storefront: no abandoned-cart capture/recovery
- **File:** Cart is session-only; no persisted abandoned-cart table or recovery email job.
- **Impact:** Carts vanish on session expiry (30 min) with no recovery path — direct lost revenue.

### H18. Storefront: product images have no width/height attributes (CLS)
- **File:** `Views/Products/Index.cshtml:174-176`, `Views/Products/Detail.cshtml:79-81`
- **Impact:** Layout shift on image load hurts Core Web Vitals (CLS) and Google Page Experience ranking signal.

---

## MEDIUM Findings (grouped)

**Database/Domain**
- `Product.Sku` / `Product.Barcode` have no unique constraint (`Models/Domain/Product.cs:18-19`).
- `ProductVariant` missing indexes on `ProductId`, `Sku`, `Barcode`.
- `StockMovement.BalanceAfter` (`:33`) is a denormalized running balance with no reconciliation/recompute job — can drift from the true sum of movements.
- `Refund.RegisterId`/`CashierUserId`, `RefundItem.ProductVariantId`/`ProductId` are loosely-typed FKs (`Refund.cs:15-17,35`).
- `StoreInventory.LastSyncedAt` set once at creation, never updated (`:20`) — dead/misleading field.
- `ParkedSale.CashierUserId`/`CustomerUserId` are plain strings, not FK-typed.

**Inventory/POS**
- `StockService.ApplyAsync` allows `QuantityOnHand` to go negative with no clamp/validation (`Services/StockService.cs:47`).
- Adjustment "reason" is free-text (`StockController.cs:113-126`), no enum — leads to inconsistent values ("Damage" vs "Damaged" vs "dmg") and blocks clean variance/loss reporting.
- `StockController.SetAll` reads current stock then applies a delta per item in a loop, theoretically racy under concurrent stocktakes for the same product/store (low real-world likelihood — stocktakes are per-branch-coordinated).

**Security**
- Cart actions (`Add`/`UpdateQuantity`/`Remove`) lack `[ValidateAntiForgeryToken]` (`Controllers/CartController.cs:34,87,115`).
- No rate limiting on transfer approve/reject/dispatch JSON endpoints.
- File upload validates by extension only, not magic bytes (`UploadController.cs:25-27`).
- No `X-Frame-Options`/`X-Content-Type-Options`/`Referrer-Policy`/CSP headers set anywhere.

**Storefront**
- No "recently viewed", "frequently bought together", or "best sellers/trending" merchandising.
- Back-in-stock notify endpoint logs the email but never triggers a notification on restock (`ProductsController.cs:216-226`).
- Cart `MaxQuantity` hardcoded to 10 (`Models/ViewModels/CartViewModel.cs:16`) — blocks bulk/gift orders.
- Sitemap omits `<image>` extension for product images (`Controllers/SeoController.cs:29-68`).
- No `[ResponseCache]`/HTTP caching on product listing/detail.
- Category sidebar counts run one query per category (N+1) (`ProductsController.cs:74-83`).

**Admin/Reports**
- Two parallel product/stock management implementations in `Areas/Admin` vs `Areas/Inventory` — consolidation needed before Admin retirement.
- Reports/list queries missing `.AsNoTracking()`.
- Transfers `SearchStock` filters `stock > 0` *after* `.Take(40)`, so autocomplete can silently return fewer than 40 results.

---

## LOW Findings (selected)
- Public Paystack key exposed in checkout view — expected/by design, no action needed.
- `AuditLog.EntityId` is `string` not FK — acceptable, polymorphic by design.
- Product image `alt` text is just the product name, not descriptive.
- Decorative SVG icons missing `aria-hidden="true"`.
- Guest checkout email field has no format validation.
- Newsletter signup CSRF token injected via JS instead of present in HTML (works, but unusual pattern).

---

## Reports Gap Analysis

| Report | Status |
|---|---|
| Reorder report (low stock) | ✅ Exists (`Areas/Inventory/Reports`) — but O(products×stores) in-memory (H10) |
| Stock value report | ✅ Exists — same perf issue |
| Sales report, Best sellers | ✅ Exists (Admin area) |
| Audit log viewer | ✅ Exists (Admin area) — export unbounded (H11), dropdowns unindexed (H12) |
| **Inventory Variance Report** | ❌ Missing — requested deliverable |
| **Dead Stock Report** (30/60/90d) | ❌ Missing — requested deliverable |
| **Inventory Aging Report** | ❌ Missing — requested deliverable |
| **Fast/Slow Moving Products** | ❌ Missing — requested deliverable |
| `InventoryVariance` entity + investigation workflow | ❌ Missing entirely — requested deliverable |
| Mandatory adjustment reason codes (enum) | ❌ Missing — currently free text |
| Granular policy-based permissions (`Inventory.View`, `.Adjust`, etc.) | ❌ Missing — currently role-based only |

---

## Phased Implementation Roadmap (proposed)

Given the size of the full request, a single uncontrolled pass is too risky for an app already in active use. Proposed phases, each independently shippable and verifiable:

- **Phase A — Critical security & data-safety (small, urgent):** rotate/remove the committed Paystack key (C1), fix the upload path-traversal (C2), set `CookieSecurePolicy.Always` + explicit `SameSite` (C6), add security headers (CSP/X-Frame-Options/etc.), add `[ValidateAntiForgeryToken]` to cart actions.
- **Phase B — Inventory integrity core:** add `RowVersion`/concurrency token to `StoreInventory` (C5); fix POS checkout (C3) and order reservation (C4) check-then-act races using transactional re-checks/row locks; fix refund double-count race (H6); wrap `ReleaseReservationAsync` in a transaction (H5); fix transfer approve/dispatch reservation races (H3/H4); clamp negative stock (M).
- **Phase C — Adjustment reason codes + `InventoryVariance` workflow:** new `AdjustmentReason` enum (Damaged/Lost/Wrong Sale/Found/Transfer Error/Count Correction/Customer Return), make it required on stock adjustments; new `InventoryVariance` entity/table + investigation workflow (Open/Investigating/Resolved/Closed) + UI.
- **Phase D — DB hardening migration:** add missing FKs/indexes (`StockReservation.ProductId/StoreId` + compound index, `Product.Sku/Barcode` unique, `ProductVariant` indexes), fix `Order.AmountTendered/ChangeGiven` precision (H1), audit log old/new-value capture (H15).
- **Phase E — New inventory reports:** Inventory Variance Report, Dead Stock Report, Inventory Aging Report, Fast/Slow Moving Products — rewritten as SQL-side aggregations (also fixes H10).
- **Phase F — Granular permission system:** introduce policy-based `Inventory.*` permissions, wire into existing role/section catalogue, decide on store-scoping (H7).
- **Phase G — Performance pass:** fix N+1s and unbounded loads (H11-H14), add `.AsNoTracking()`, paginate exports, batch the stock/stocktake apply loops.
- **Phase H — Storefront conversion & SEO:** product reviews (model+UI+schema), recently-viewed/related products, abandoned-cart capture, back-in-stock email job, image dimensions/lazy-loading cleanup, sitemap image extension, cart max-quantity config.
- **Phase I — Tests:** concurrency tests for Phase B fixes (simultaneous sales/transfers/refunds), permission tests for Phase F, report-accuracy tests for Phase E.

Each phase = code + migration (where relevant) + build + HTTP/psql verification against the running dev app, following the methodology already used for the transfer workflow.
