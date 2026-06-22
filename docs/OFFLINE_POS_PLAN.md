# Offline ePOS Plan

**Status:** Proposed — decisions pending (see [Decisions to lock](#decisions-to-lock-before-any-code)). No code written yet.
**Goal:** Turn the existing web POS into an installable app that keeps selling during internet / network outages, so a website or host hiccup never stops the till.

---

## Context — how the POS works today

- The POS is a server-rendered, client-heavy web page ([`Views/Pos/Sell.cshtml`](../src/SterlingLams.Web/Views/Pos/Sell.cshtml)) that calls JSON endpoints on [`PosController`](../src/SterlingLams.Web/Controllers/PosController.cs) (`Search`, `StockLookup`, `Categories`, `Checkout`, `Hold`/`RecallParkedSale`, `CustomerSearch`, `DiscountReasons`, …).
- Checkout deducts stock **server-side** under a Postgres `SELECT … FOR UPDATE` row lock + `StockService.ApplyAsync` ([`Checkout`](../src/SterlingLams.Web/Controllers/PosController.cs#L738)), so online it **cannot oversell**.
- **Payments are manual labels.** `order.PaymentProvider = req.PaymentMethod` — cash / card / transfer / USSD / Opay are just recorded; the actual card/transfer happens on a separate bank terminal/app. The till also records cash tendered + change.
- Sign-in is a cashier **PIN** verified against the server.
- One shared PostgreSQL database is the single source of truth.

### Why this matters
Because the till only **records** the payment method (no in-app card processing), **every payment type already works offline.** The usual "offline POS = cash only" limitation does **not** apply here. The only genuinely hard offline problem is **stock accuracy** (see below).

---

## What offline POS will / won't do

**Works offline**
- Sign in to an already-open till (cached shift session)
- Search / scan the catalog, build a cart, apply discounts
- Take **any** payment method (recorded, same as online)
- Complete the sale, print a receipt
- Park / recall sales
- Open / close the cash session (cash counting is local anyway)

**Needs internet**
- In-app **refunds** (touch the original order + payment + stock — kept online)
- Pulling **fresh** prices / stock
- The final **stock commit** to the server (happens automatically on reconnect)

---

## Technology choice: PWA (recommended)

Make the existing web POS a **Progressive Web App** rather than a native app:
- **Installable** to a tablet home screen / desktop; opens full-screen like a real app.
- One codebase across Windows PC / Android tablet / iPad — no app store, no separate native project.
- Reuses today's POS page, which already has the right shape (client UI + JSON endpoints).

Native (MAUI / React Native) or a desktop wrapper (Tauri / Electron) is more work for hardware-control benefits we don't need yet. Revisit only if the PWA hits a device wall.

---

## Architecture (four moving parts)

1. **Service worker + web app manifest** — cache the POS "shell" (HTML/CSS/JS/fonts) so the till loads instantly with zero network, and make it installable.
2. **On-device database (IndexedDB)** — a snapshot of catalog, prices, variants, barcodes, customers, discount reasons, and **stock levels**, refreshed whenever online. The POS reads from this so it keeps working when the network drops.
3. **Offline sale queue** — a completed offline sale is written locally with a **client-generated unique ID**, the local stock snapshot is decremented, and a receipt prints from local data.
4. **Sync on reconnect** — queued sales are pushed to a new server endpoint that applies them **idempotently** (the unique ID prevents double-posting on flaky networks), commits stock deductions under lock, and assigns real order numbers. Then a fresh snapshot is pulled down.

---

## The hard problem: offline stock accuracy

Online, checkout holds a `FOR UPDATE` lock and cannot oversell. **Offline, no app can hold that lock** — the till only has a cached stock number.

- Risk case: two tills at the **same branch**, both offline, both sell the last unit → oversell.
- During a real outage, online orders mostly can't happen anyway (card payment needs internet), so the dominant risk is multi-till-same-branch.

**Stance:** offline stock is **best-effort, not guaranteed** — the same as Square / Shopify POS. On sync the server:
- deducts under lock,
- **clamps** anything that would go negative to 0,
- **flags oversold lines** on a reconciliation screen for staff to resolve (refund / reorder).

For a jeweller with low per-item counts and usually one till per branch, real-world oversell risk is low. Optional guard: when offline, **warn or block** on selling the **last 1–2** of an item.

---

## Other considerations

- **Offline sign-in:** simplest safe approach — whoever opens the till stays signed in for the shift (cached offline session). Offline **user-switching** needs pre-synced PIN hashes on the device (more sensitive) — defer unless required.
- **Order / receipt numbers:** offline sales get a provisional device-prefixed number, linked to the real server number on sync (no collisions).
- **Device security:** the device holds catalog + customers + the sale queue locally, so it must be trusted — screen lock, clear-on-logout, HTTPS only. Short policy to be written.
- **Refunds:** online-only for now.
- **Session close / Z-report:** offline-aware; syncs on reconnect.
- **iOS Safari:** Background Sync is limited, so we also sync on reconnect in the foreground.

---

## Phased plan (each phase ships value)

| Phase | Delivers | Notes / effort |
|------|----------|----------------|
| **0 — Installable app** | Manifest + service worker caching the app shell. POS installs like an app and loads instantly. | No offline selling yet, but it's "an app." Small effort. |
| **1 — Offline catalog & cart** | Cache catalog/prices/barcodes/customers/discount-reasons + stock snapshot locally; Sell page reads from it; online/offline banner + "last synced." | Search, scan, build a cart with no network. Bulk of work starts here (extract the inline `Sell.cshtml` JS into a cacheable "read local, refresh online" data layer). |
| **2 — Offline sales + queue + sync** | Record sales locally with unique IDs, decrement local stock, print receipts, "pending sync (n)" badge; idempotent server ingest endpoint; auto-sync on reconnect + manual "Sync now." | True offline selling. |
| **3 — Reconciliation & safety** | Server clamps/flags oversold lines on a reconciliation screen; stale price/stock warnings; last-1–2 guard. | Trustworthy stock after outages. |
| **4 — Hardening** | Offline session policy, local-data security, optional offline user-switch, receipt-printer / cash-drawer checks, iOS sync quirks. | Smaller follow-ups. |

**Suggested first deliverable:** Phase 0 + 1 together.

---

## Decisions to lock before any code

| # | Decision | Options | Chosen |
|---|----------|---------|--------|
| 1 | Till **device(s)** | Windows PC / Android tablet / iPad (iPad/Safari has the most PWA quirks) | _pending_ |
| 2 | **Tills per branch** | One / several (drives oversell guard) | _pending_ |
| 3 | **Offline oversell policy** | (a) best-effort + reconcile (recommended) / (b) hard-block last 1–2 offline | _pending_ |
| 4 | **Offline user-switching** | Not needed (one cashier per device/shift) / required (pre-synced PIN hashes) | _pending_ |
| 5 | **Starting phase** | Phase 0 + 1 (recommended) / other | _pending_ |

---

## Out of scope (for now)
- Splitting storefront / inventory / POS into separately-hosted services with separate databases — evaluated and rejected: the shared database is the real fate-line, and separate datastores would break stock consistency. Resilience is better gained via hosting redundancy (paid tier, ≥2 instances, zero-downtime deploys) + this offline POS. See conversation notes / a future `ARCHITECTURE_DECISION.md`.
- In-app card processing at the till (payments stay manual records).
- Offline refunds.
