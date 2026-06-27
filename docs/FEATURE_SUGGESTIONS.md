# Sterlin Glams — Feature Review & Suggestions

_Project review and recommended additions. Last updated: 2026-06-27._

> **Out of scope (deliberately excluded):** Suppliers / purchasing, Tax / VAT, and
> Manufacturing / bill-of-materials. None of the suggestions below introduce these.

---

## 1. Where the platform is today

A genuinely full-featured jewellery commerce platform. Already built:

**Storefront**
- Product catalogue, category pages, filtering/sorting, search (`/api/search`)
- Product detail with variants, image gallery, JSON-LD structured data, sitemap
- Cart, checkout (delivery + store pickup), guest checkout
- Payments: Paystack, Flutterwave, Stripe
- Wishlist, Recently Viewed, Best Sellers, Trending (toggleable)
- **Compare** (new), **Quick View**, hover actions
- Order tracking, pickup QR pass, abandoned-cart capture, back-in-stock alerts
- Loyalty points, discount codes, newsletter capture
- Hero campaign **slider** + Featured **carousel** (admin-tuned timing)

**Admin console**
- Orders (+ refunds), customers, products, categories, attributes, discounts
- Marketing (abandoned carts / back-in-stock), email customizer
- Reports, dashboard, audit log, roles & permissions, settings
- **SEO description generator** (per-category, preview → apply)
- Image upload (Cloudinary), CSV import/export, WooCommerce/catalog/barcode import

**Inventory System (own POS + back office)**
- POS/till sessions, registers, cash management, parked sales, refunds
- Stock Management (per-branch grid), per-location Min/Max/On-order/Alerts
- Inter-branch transfers (confirmation workflow), Track Stock modal
- Stock Takes (count → review → complete) + history & details
- Reports: Stock Levels, Warnings, Discrepancies, Reorder, Movements, Shrinkage, Sales
- Short, sequential order numbers (SL-/POS- shared counter)

**Foundations:** ASP.NET Core 9, EF Core + PostgreSQL, Identity (roles/permissions,
2FA scaffolding), rate limiting, audit logging, data-protection keys, Render deploy.

---

## 2. High-impact additions (recommended next)

### 2.1 Product Reviews & Ratings ⭐ _(High value, Medium effort)_
No review/rating system exists today. For jewellery this is a major conversion and
SEO lever.
- Star rating + written review per product; "verified buyer" badge (links to an order).
- Average rating on cards + detail; aggregate-rating JSON-LD (rich snippets in Google).
- Admin moderation queue (approve / hide / reply).
- Optional photo reviews.

### 2.2 Customer Account hub _(High value, Low–Medium effort)_
Strengthen the logged-in experience:
- **Order history** with statuses + **one-click reorder** (re-add items to cart).
- Saved **delivery addresses** book (add/edit/default).
- Loyalty balance + history; wishlist; back-in-stock subscriptions in one place.
- Re-download/track pickup pass.

### 2.3 Gift cards & e-vouchers _(High value, Medium effort)_ — ✅ v1 SHIPPED
- **Done:** Admin issue/manage (unique code + balance, deactivate, manual adjust, ledger),
  public balance-check page (`/gift-cards`, rate-limited), partial redemption at online
  checkout (earmark at placement → draw on payment success, idempotent), expiry rules,
  full-refund returns the drawn balance. Setting `giftcards.enabled` gates redemption.
- **Deferred:** POS redemption; purchasable gift-card-as-product; full gift-card payment
  (a ≥₦1 remainder is always charged so the gateway has a positive amount — zero-total
  checkout needs a separate gateway-bypass path).

### 2.4 Gifting at checkout _(Medium value, Low effort)_
- "This is a gift" → gift message + gift wrap option (small fee), hide prices on the
  packing slip. Strong fit for the jewellery audience.

### 2.5 Storefront merchandising polish _(Medium value, Low effort)_
- **Related / "You may also like"** on product detail (same category / co-bought).
- **Low-stock urgency** ("Only 2 left") using existing stock data.
- **Size/length guide** for rings, anklets, necklaces (modal partial).
- Recently restocked row (data already tracked).

---

## 3. Marketing & SEO

- **Per-category & per-product SEO meta editor** (title/description overrides) — the
  generator now writes meta summaries; add manual override fields.
- **Product structured data**: extend existing JSON-LD with `aggregateRating` once
  reviews ship, and `availability`/`price` (verify completeness).
- **Promotions/banner scheduler**: schedule sale prices and homepage banners with
  start/end dates (sale price exists; add scheduling).
- **Referral / "refer a friend"** rewards via the loyalty engine.
- **Blog / lookbook** (content pages) for SEO + storytelling — lightweight CMS or
  markdown pages.
- **Cart/checkout recovery**: abandoned-cart capture exists — add the automated
  recovery **email send** + a one-click restore link if not already scheduled.

---

## 4. Inventory / POS (no supplier, no tax, no manufacturing)

The inventory system is already strong. Suggested refinements that stay within scope:

- **Low-stock email alerts**: the per-location `StockAlerts` flag + Min exist — wire a
  background job that emails staff when on-hand ≤ Min (Stock Warnings already lists them).
- **Reorder worksheet**: use Min/Max/On-order to compute suggested reorder quantities
  (Max − OnHand − OnOrder) as a printable/exportable list — _without_ a supplier/PO.
- **Cycle counts**: schedule recurring partial stock-takes (e.g. by category/branch).
- **Barcode label batching from low-stock/Stock Take** (label tool exists).
- **POS day-end / X & Z shift reports**: cash reconciliation summary per register/shift
  (cash movements are tracked — surface a report).
- **Stock valuation at sale price** (cost intentionally excluded): total units × sale
  price per branch (the Stock Levels report already shows sale value — add a summary).
- **Variant-level analytics**: best/worst selling colours/sizes (variant stock now tracked).

---

## 5. Admin & operations

- **Bulk product actions**: multi-select → archive, set category, set sale price,
  feature/unfeature, run SEO generator on a selection (generator already exists).
- **Scheduled price / sale** (start + end datetime) per product.
- **Saved report exports** + email a report on a schedule.
- **Better media management**: bulk image upload + drag-reorder + alt-text (alt helps SEO).
- **Activity dashboard widgets**: today's sales by channel, low-stock count, pending
  transfers, abandoned carts (some exist on the Inventory Overview — unify on Admin).

---

## 6. Performance, reliability & security

- **Health checks** — ✅ DONE. `/health` (liveness) + `/health/ready` (DB reachability).
- **Output/response caching** — ✅ DONE (home + category lists, 60s TTL, tag-evicted on
  product/settings edits). Per-user bits (cart/wishlist/auth/CSRF token) load client-side
  from `/site/header-state`; TempData moved to session so pages stay cookie-free/cacheable.
  **Deferred:** product **detail** caching — it carries per-user review-form + wishlist
  state that needs the same client-side treatment first.
- **Image optimisation**: serve responsive sizes / `srcset` + AVIF/WebP via Cloudinary
  transforms; lazy-load below the fold (partly done).
- **Error monitoring** — ✅ DONE (Sentry, gated by `Sentry:Dsn`; add the DSN in Render to
  activate). Client-side (browser) Sentry still optional.
- **Automated DB backups** verification (Render Postgres) + a documented restore drill.
- **2FA**: confirm the Identity 2FA flow is fully wired for admin/staff accounts and
  enforce it for the Admin role.
- **Security headers audit**: CSP is in place (nonces) — review HSTS, referrer-policy,
  permissions-policy.

---

## 7. Technical / DevOps

- **CI pipeline**: build + run the existing test suite on every push (8 test files exist);
  block deploy on red.
- **Staging environment** mirroring prod for safe testing before Render production.
- **Dev → Prod data sync**: a documented, repeatable process for product/content data
  (this is currently a manual gap — the SEO tool now runs directly on prod, which helps).
- **Test coverage growth**: add tests around checkout, stock concurrency, transfers,
  order-number generation, and the new compare/SEO tools.
- **Feature flags**: the settings system already acts as lightweight flags — formalise a
  few (e.g. enable reviews, gift cards) for safe rollout.

---

## 8. Suggested priority order

| # | Item | Value | Effort |
|---|------|-------|--------|
| 1 | Product Reviews & Ratings (+ rich snippets) | ★★★ | ●● |
| 2 | Customer Account hub (orders, reorder, addresses) | ★★★ | ●● |
| 3 | Low-stock email alerts + Reorder worksheet | ★★★ | ● |
| 4 | Health checks + response caching + error monitoring | ★★★ | ● |
| 5 | Gift cards & gifting at checkout | ★★ | ●● |
| 6 | Related products + low-stock urgency + size guide | ★★ | ● |
| 7 | Bulk product actions + scheduled sales | ★★ | ●● |
| 8 | CI + staging + more tests | ★★ | ●● |
| 9 | Blog/lookbook, referral programme | ★ | ●● |

★ = customer/business value · ● = build effort

---

_Generated as a living document — update as items are delivered._
