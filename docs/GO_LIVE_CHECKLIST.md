# Sterlin Glams — Go-Live Checklist

Everything needed to take the storefront + back-office live. Recommended stack:
**Render (paid Starter) for the app · Neon (or Render) Postgres · Cloudinary for images ·
Cloudflare in front.** No code changes — this is all hosting + configuration.

On Render, nested settings use **double underscores** (`Payment__Paystack__SecretKey`), and arrays
use an index (`Logistics__DeliveryStates__0`).

---

## 1. Hosting

- [ ] App on a **paid** plan (Render Starter ~$7/mo). The free tier sleeps → slow cold starts +
      poor uptime; a paid tier is required for real ~99.9% uptime.
- [ ] Managed **PostgreSQL** (Neon, or Render Postgres paid — note Render's *free* DB expires after
      90 days, don't use it for production).
- [ ] **Cloudinary** account (free tier is fine to start) for media.
- [ ] **Cloudflare** account with your domain.

---

## 2. Environment variables (set on the host)

### Database — pick ONE
- [ ] `DATABASE_URL` = `postgres://user:pass@host:5432/db` (Render auto-sets this when you link its
      DB; Program.cs converts it to Npgsql), **or**
- [ ] `ConnectionStrings__DefaultConnection` = `Host=...;Port=5432;Database=...;Username=...;Password=...;SSL Mode=Require;Trust Server Certificate=true`

### App
- [ ] `ASPNETCORE_ENVIRONMENT` = `Production`
- [ ] `App__BaseUrl` = `https://sterlinglams.com`  *(absolute links in emails, unsubscribe, abandoned-cart, logistics)*

### Images (Cloudinary) — so the site stays fast
- [ ] `Cloudinary__CloudName`
- [ ] `Cloudinary__ApiKey`
- [ ] `Cloudinary__ApiSecret`
  *(Without these, uploads fall back to local disk, which is wiped on every redeploy — so these are required in production.)*

### Payments (Paystack — live keys)
- [ ] `Payment__Provider` = `Paystack`
- [ ] `Payment__Paystack__SecretKey` = `sk_live_...`
- [ ] `Payment__Paystack__PublicKey` = `pk_live_...`
- [ ] `Payment__Paystack__WebhookSecret` = *(from Paystack)*

### Email (SMTP) — required for order emails, campaigns & automations
- [ ] `Email__Enabled` = `true`
- [ ] `Email__Host`, `Email__Port` (usually 587), `Email__Username`, `Email__Password`
- [ ] `Email__FromAddress` = e.g. `hello@sterlinglams.com`
- [ ] `Email__FromName` = `Sterlin Glams`
- [ ] `Email__EnableSsl` = `true`
  *(Until this is set, emails are written to a pickup folder, not delivered.)*

### Lagos delivery integration (optional — enable when ready)
- [ ] `Logistics__Enabled` = `true`
- [ ] `Logistics__SharedSecret` = *(same secret as the logistics app's `STORE_WEBHOOK_SECRET`)*
- [ ] `Logistics__PushUrl` = `https://sterlinglamslogistics.com/api/external-orders`
  *(see `docs/LOGISTICS_INTEGRATION.md`)*

### Error monitoring (optional)
- [ ] `Sentry__Dsn` = *(your Sentry DSN; leave unset to keep it off)*

---

## 3. Database migrations — the deploy step  ⚠️ required

The app **fails to start in Production while migrations are pending**. Apply them on each deploy.

- [ ] **Option A (recommended):** Render **Pre-Deploy Command**:
      `dotnet ef database update --project src/SterlingLams.Web`
      (or build a `dotnet ef migrations bundle` and run it), **or**
- [ ] **Option B (simplest):** set `Database__AutoMigrate` = `true` so the app migrates on startup.

First deploy auto-seeds roles, stores, categories, settings and an admin user.

---

## 4. Cloudflare

- [ ] Point your domain's nameservers to Cloudflare; add the app host as a proxied (orange-cloud)
      DNS record.
- [ ] **SSL/TLS mode = Full (strict)** ⚠️ — the app forces HTTPS, so *Flexible* causes redirect
      loops. (App already honours `X-Forwarded-Proto`, so it works behind the proxy.)
- [ ] Always Use HTTPS = on; Auto Minify (JS/CSS) optional.
- [ ] Caching: default is fine — the storefront already sets cache headers + output-caches the
      home/category pages; static assets are content-hashed and cached a year.
- [ ] `AllowedHosts` in `appsettings.Production.json` is `sterlinglams.com;www.sterlinglams.com` —
      update if your domain differs.

---

## 5. Third-party dashboards

- [ ] **Paystack:** add webhook `https://sterlinglams.com/webhooks/paystack`; set the callback/
      success URL to your domain; switch to **live** keys.
- [ ] **Cloudinary:** nothing beyond the keys above (folders are auto-created).
- [ ] **Logistics (Vercel), if enabling:** set `STORE_WEBHOOK_SECRET` (= `Logistics__SharedSecret`)
      and `STORE_DELIVERED_WEBHOOK_URL` = `https://sterlinglams.com/webhooks/logistics/delivered`.

---

## 6. Security hardening (do before/at launch)

- [ ] **Change the seeded admin password** immediately (default is for first login only).
- [ ] Confirm no secrets are in git (they aren't — all prod values come from env;
      `appsettings.Development.json` is git-ignored).
- [ ] Consider the deferred "secret staff URLs" idea (rename `/Admin`, `/Inventory`, `/Marketing`,
      `/Pos` via env) — optional.
- [ ] Data Protection keys persist in the DB by default (survives redeploys) — nothing to do unless
      you set `DataProtection__KeysPath`.

---

## 7. Smoke test after deploy

- [ ] `GET /health` → `Healthy`; `GET /health/ready` → `Healthy` (DB reachable).
- [ ] Storefront loads over HTTPS; a product image loads from `res.cloudinary.com`.
- [ ] Place a small **test order** end-to-end (Paystack live) → payment confirms → order appears in
      Admin → confirmation email received.
- [ ] Admin (`/Admin`), Inventory (`/Inventory`), Marketing (`/Marketing`) each load and gate by role.
- [ ] Send a tiny **test campaign** to yourself from the Marketing Hub → arrives with a working
      unsubscribe link.
- [ ] (If enabled) a Lagos delivery order shows in the logistics dashboard; marking it delivered
      flips the order to Delivered here.

---

_Smallest path to live: Render Starter + Neon + Cloudinary keys + Cloudflare (Full strict) +
migrations applied + SMTP + Paystack live keys. ≈ $7–12/month._
