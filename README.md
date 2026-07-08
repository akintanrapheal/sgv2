# Sterlin Glams — E-commerce + Inventory/POS Platform

> **© 2026 Sterlin Glams. All rights reserved. Proprietary and confidential.**
> This software is proprietary. No use, copying, modification, or distribution is
> permitted without the prior written permission of the Owner. Access to this repository
> does not grant any licence. See [`LICENSE`](LICENSE). Contact: rapheal@sterlinglamslogistics.com

A self-contained jewellery & accessories platform: a customer storefront, an admin
back office, and an in-house multi-branch **inventory system with a point-of-sale till**.
Stock, orders, payments and reporting all live in one PostgreSQL database — there is no
external ERP.

> Previously integrated with ERPNext; that has been retired and replaced by the in-house
> Inventory System. The website database is now the single source of truth for stock.

---

## Tech Stack

| Layer | Technology |
|---|---|
| Framework | ASP.NET Core 9 (Razor MVC) |
| Database | PostgreSQL (EF Core 9 / Npgsql) |
| Auth | ASP.NET Identity (cookie) — role + section based |
| Payments | Paystack (primary), Stripe, Flutterwave |
| Background jobs | `BackgroundService` (reservation sweep, fulfilment retry, low-stock alerts) |
| Email | SMTP via `IEmailService` (no-ops until SMTP is configured) |
| Styling | Tailwind CSS |
| Hosting | Docker-ready |

---

## The three surfaces

| Surface | Route | Who |
|---|---|---|
| **Storefront** | `/` | Customers (and guests) |
| **Admin back office** | `/Admin` | Admin + staff roles |
| **Inventory System** | `/Inventory` | Inventory staff / admin |
| **POS Till** | `/Till` | Cashiers (per-register, PIN sign-in) |

---

## Run Locally

1. Install the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) and PostgreSQL.
2. Create a database: `CREATE DATABASE sterlinglams_dev;`
3. Configure secrets (see below).
4. Run:

```bash
dotnet run --project src/SterlingLams.Web
```

The app **auto-migrates and seeds** on first run (roles, stores, categories, attributes,
and site settings).

---

## Configuration

`appsettings.json` ships with placeholders only. For local dev, copy
`appsettings.Development.json` (gitignored) or use .NET user secrets — **never commit secrets**:

```bash
cd src/SterlingLams.Web
dotnet user-secrets init

# PostgreSQL
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=sterlinglams_dev;Username=postgres;Password=YOUR_PASSWORD"

# Payments
dotnet user-secrets set "Payment:Provider"               "Paystack"
dotnet user-secrets set "Payment:Paystack:SecretKey"     "sk_live_..."
dotnet user-secrets set "Payment:Paystack:PublicKey"     "pk_live_..."
dotnet user-secrets set "Payment:Paystack:WebhookSecret" "your_webhook_secret"

# Email (SMTP) — required for order confirmations, password reset,
# email confirmation, low-stock & fulfilment alerts. Until set, emails are skipped + logged.
dotnet user-secrets set "Email:Enabled"     "true"
dotnet user-secrets set "Email:Host"        "smtp.yourhost.com"
dotnet user-secrets set "Email:Port"        "587"
dotnet user-secrets set "Email:Username"    "..."
dotnet user-secrets set "Email:Password"    "..."
dotnet user-secrets set "Email:FromAddress" "no-reply@sterlinglams.com"
```

Most operational toggles (store open/closed, maintenance mode, pickup, loyalty rates,
shipping fees, homepage copy, notifications) are **managed at runtime** in
**Admin → Settings**, not in config.

---

## Database Migrations (EF Core)

```bash
cd src/SterlingLams.Web
dotnet tool install --global dotnet-ef   # once per machine
dotnet ef database update
```

On startup the app automatically runs pending migrations and seeds roles, the 3 stores
(Abuja, Allen, Ikota), categories, attributes, and site settings.

---

## Roles & Access

| Role | Access |
|---|---|
| **Admin** | Everything (bypasses all section checks) |
| **Operations** | Dashboard, Orders, Inventory, Stores |
| **Sales** | Dashboard, Orders, Customers, Discounts |
| **Inventory** | Dashboard, Products, Inventory, Stores, Categories, Attributes (works in `/Inventory`) |
| **Social Media** | Dashboard, Products |
| **Customer** | Storefront only |

Access is **section-based** per role; inventory writes are additionally constrained by
**store-level authorization** (a user with assigned stores can only mutate stock for those
branches; no assignment = unrestricted legacy behaviour; Admin bypasses).

Grant yourself admin after registering:

```sql
INSERT INTO "AspNetUserRoles" ("UserId", "RoleId")
SELECT u."Id", r."Id"
FROM "AspNetUsers" u CROSS JOIN "AspNetRoles" r
WHERE u."Email" = 'your@email.com' AND r."Name" = 'Admin';
```

---

## Key Features

**Inventory System (`/Inventory`)**
- Per-product **and per-variant** stock across branches (effective-row fallback model)
- Stock grid with bulk editing, barcode scan lookup, adjustment reasons
- **Stock-take** sheets (per-variant counting) and **inter-branch transfers** (request → approve → dispatch → receive, all under row locks)
- **Reports** (reorder, stock value, sales, best sellers) aggregated in SQL
- Append-only `StockMovement` ledger; running balances in `StoreInventory`

**POS Till (`/Till`)**
- Register-bound, PIN sign-in, cash/card sale, change due, parked sales, refunds/returns
- Sells against **available** stock (on-hand − reserved) so it can't oversell online holds

**Orders & fulfilment**
- Online checkout (delivery or store pickup), guest checkout, discount codes, delivery zones
- Multi-branch fulfilment with atomic stock reservations + `FOR UPDATE` row locks
- Background **fulfilment retry + admin alert** for paid-but-unfulfilled orders
- Online + POS **refund workflows** (records refund, returns stock, attempts gateway refund)
- Hardened Paystack webhook (HMAC + exact order match + amount check)

**Merchandising & loyalty**
- Best sellers, trending, new arrivals, recently viewed, **frequently bought together**, **save for later**
- **Loyalty points** — earn on paid orders + **redeem at checkout** (admin-tunable rates)

**Platform**
- Runtime site settings (store toggles, maintenance mode, shipping, homepage, notifications)
- Low-stock email digest, security headers/CSP, short-lived staff sessions, audit log

---

## Project Structure

```
src/SterlingLams.Web/
├── Areas/
│   ├── Admin/                # Admin back office (/Admin/*)
│   └── Inventory/            # In-house inventory system (/Inventory/*)
├── Controllers/              # Storefront + Till (POS) + Webhooks
├── Models/
│   ├── Domain/               # EF Core entities
│   └── ViewModels/
├── Services/                 # Stock, Fulfilment, Transfers, Loyalty, Merchandising,
│   │                         #   Discounts, Settings, Permissions, StoreAccess, Email, Audit
│   └── Payment/              # Paystack / Stripe / Flutterwave
├── Infrastructure/           # Background services, middleware, seeders
│   ├── ReservationSweeper.cs
│   ├── FulfilmentRetryService.cs
│   ├── LowStockAlertService.cs
│   ├── MaintenanceModeMiddleware.cs
│   └── *SeedData.cs
├── Data/                     # ApplicationDbContext + Migrations
├── Views/                    # Storefront Razor views
└── wwwroot/                  # Tailwind output (app.css), JS, uploads
```

---

## Notes

- Ongoing platform audit + fixes are tracked in [`docs/AUDIT_FIX_TRACKER.md`](docs/AUDIT_FIX_TRACKER.md).
- Imports: WooCommerce catalogue migration and EposNow barcode import utilities exist under `Services/` and `tools/`.

---

## Deployment

Hosted on **Azure App Service (Linux)** with **Azure Database for PostgreSQL**, deployed
via GitHub Actions on every push to `main`. See [`docs/AZURE_DEPLOYMENT.md`](docs/AZURE_DEPLOYMENT.md)
for the full setup and the [`.github/workflows/azure-deploy.yml`](.github/workflows/azure-deploy.yml)
pipeline.

---

## Developer

Designed and built by **Dev Rapheal**.

Copyright © Sterlin Glams. All rights reserved.
