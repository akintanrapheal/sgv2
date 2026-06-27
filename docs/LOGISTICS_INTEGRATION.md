# Sterlin Glams Store ⇄ Lagos Delivery Integration

Two-way, HMAC-signed link between the store (**sterlinglams.com**, this repo) and the Lagos
delivery app (**sterlinglamslogistics.com**, Next.js on Vercel). Matched on the order number.

1. **Store → Logistics (push):** when a paid online **Lagos delivery** order is confirmed, the
   store POSTs it to the courier and it shows up as an unassigned order in the dispatch dashboard.
2. **Logistics → Store (delivered callback):** when a driver marks it delivered, the courier POSTs
   back and the store flips that order to **Delivered** automatically.

Both calls are signed with **one shared secret** (HMAC-SHA256, base64, header `x-sg-signature`).

---

## 1. Merge the logistics PR

Branch: `feat/sterlinglams-store-integration` on the logistics repo. Let Vercel build a **preview**
first (that repo has no CI), confirm it compiles, then merge.

## 2. Generate one shared secret

```bash
openssl rand -hex 32
```

The **same** value goes in both dashboards.

## 3. Store side — Render (this app)

### 3a. Environment variables

| Key | Value |
|-----|-------|
| `Logistics__Enabled` | `true` |
| `Logistics__SharedSecret` | *(the secret from step 2)* |
| `Logistics__PushUrl` | `https://sterlinglamslogistics.com/api/external-orders` |

`Logistics__DeliveryStates__0` defaults to `Lagos`; add `Logistics__DeliveryStates__1=Ogun`, etc.
to serve more states (empty = push every delivery order).

### 3b. Deploy step — apply the EF migrations  ⚠️ required

The integration adds `Order.LogisticsPushedAt` (plus other migrations from recent work). **The app
fails to start in Production while migrations are pending**, so they must be applied as part of the
deploy. Choose one:

**Option A — run the migrations as a release/deploy command (recommended):**

```bash
# from the repo root, against the production connection string
dotnet ef database update --project src/SterlingLams.Web

# or apply a generated SQL bundle produced at build time:
dotnet ef migrations bundle --project src/SterlingLams.Web -o migrate
./migrate --connection "$DATABASE_URL"
```

On Render, add this as a **Pre-Deploy Command** (Settings → Build & Deploy) so it runs before the
new instance starts.

**Option B — migrate on startup (simplest):** set the env var

| Key | Value |
|-----|-------|
| `Database__AutoMigrate` | `true` |

The app then applies pending migrations itself on boot. (Fine for a single instance; for multiple
instances prefer Option A so they don't race.)

Then trigger a redeploy.

## 4. Logistics side — Vercel (Next.js app)

| Key | Value |
|-----|-------|
| `STORE_WEBHOOK_SECRET` | *(the same secret from step 2)* |
| `STORE_DELIVERED_WEBHOOK_URL` | `https://sterlinglams.com/webhooks/logistics/delivered` |

Use whatever URL the store is publicly reachable at — if the custom domain isn't live, use
`https://sterlinglams.onrender.com/webhooks/logistics/delivered`. Redeploy.

## 5. Test end-to-end

1. Place a **Lagos delivery** order on the store and pay.
2. It appears as an **unassigned** order in the dispatch dashboard within seconds (`SL-…`).
3. Assign a driver → driver marks it **Delivered** in the driver app.
4. The store admin shows that order as **Delivered** automatically.
5. A **non-Lagos** order should *not* appear in logistics (filtered out).

## Gotchas

- **One secret, two places** — if `Logistics__SharedSecret` ≠ `STORE_WEBHOOK_SECRET`, every call is
  rejected with **401**. This is the #1 thing to check.
- **URL direction** — `Logistics__PushUrl` points *to logistics*; `STORE_DELIVERED_WEBHOOK_URL`
  points *to the store*.
- **Idempotent + best-effort** — the push runs once per order (`Order.LogisticsPushedAt`) and never
  breaks checkout; the delivered callback is a no-op if the order is already Delivered, and unknown
  orders (e.g. legacy WooCommerce) are acked and ignored. Safe to leave both integrations running.
- **No duplicate customer messages** — the delivery WhatsApp/SMS/email is sent only by the logistics
  app; the store does not re-notify on the delivered callback.
