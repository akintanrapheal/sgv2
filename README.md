## SterlingLams.com — Build Notes

### Tech Stack

| Layer | Technology |
|---|---|
| Framework | ASP.NET Core 9 (Razor MVC) |
| Database | PostgreSQL 18 (EF Core / Npgsql) |
| ERP | ERPNext (Frappe Cloud REST API) |
| Payments | Paystack (primary), Stripe, Flutterwave |
| Styling | Tailwind CSS |
| Hosting | Docker-ready |

---

### Run Locally

1. Install [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) and PostgreSQL 18
2. Create a database: `CREATE DATABASE sterlinglams_dev;`
3. Set secrets (see Configuration section below)
4. Run:

```bash
dotnet run --project src/SterlingLams.Web
```

The app auto-migrates and seeds data on first run.

---

### Configuration

Use .NET user secrets to store credentials locally (never commit secrets):

```bash
cd src/SterlingLams.Web
dotnet user-secrets init

# PostgreSQL
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=sterlinglams_dev;Username=postgres;Password=YOUR_PASSWORD"

# ERPNext
dotnet user-secrets set "ERPNext:BaseUrl"         "https://your-instance.frappe.cloud"
dotnet user-secrets set "ERPNext:ApiKey"          "your_api_key"
dotnet user-secrets set "ERPNext:ApiSecret"       "your_api_secret"
dotnet user-secrets set "ERPNext:DefaultCustomer" "Walk-In Customer"

# Paystack
dotnet user-secrets set "Payment:Provider"               "Paystack"
dotnet user-secrets set "Payment:Paystack:SecretKey"     "sk_live_..."
dotnet user-secrets set "Payment:Paystack:PublicKey"     "pk_live_..."
dotnet user-secrets set "Payment:Paystack:WebhookSecret" "your_webhook_secret"

# Optional payment providers
dotnet user-secrets set "Payment:Stripe:SecretKey"       "sk_live_..."
dotnet user-secrets set "Payment:Stripe:PublishableKey"  "pk_live_..."
dotnet user-secrets set "Payment:Stripe:WebhookSecret"   "whsec_..."
dotnet user-secrets set "Payment:Flutterwave:SecretKey"  "FLWSECK-..."
```

---

### Database Migrations (EF Core)

```bash
cd src/SterlingLams.Web

# Install EF tools (once per machine)
dotnet tool install --global dotnet-ef

# Apply migrations to database
dotnet ef database update
```

On startup the app will automatically:
1. Run pending EF migrations
2. Seed roles (Admin, Customer)
3. Seed the 3 store locations (Abuja, Allen, Ikota)
4. Seed product categories

---

### Granting Admin Access

After registering your account, run this SQL:

```sql
INSERT INTO "AspNetUserRoles" ("UserId", "RoleId")
SELECT u."Id", r."Id"
FROM "AspNetUsers" u
CROSS JOIN "AspNetRoles" r
WHERE u."Email" = 'your@email.com'
  AND r."Name"  = 'Admin';
```

Then visit `/Admin/Dashboard`.

---

### ERPNext Integration

This project uses [ERPNext](https://erpnext.com/) (Frappe Cloud) as the ERP backend via its REST API.

**Authentication:** Token-based (`Authorization: token api_key:api_secret`)

**Two-way inventory sync:**

| Direction | Mechanism |
|---|---|
| ERPNext → Website | `InventorySyncHostedService` polls ERPNext `Bin` records every 60 seconds and updates local `StoreInventories` |
| Website → ERPNext | On order confirmation, `CheckoutController` creates an ERPNext **Sales Order** (submitted) and a **Stock Entry (Material Issue)** to deduct actual warehouse stock |

**ERPNext document mapping:**

| Website concept | ERPNext document |
|---|---|
| Store | Warehouse (`ErpNextWarehouse` field) |
| Product | Item (`ErpNextItemCode` field) |
| Order | Sales Order (`SAL-ORD-*`) |
| Stock deduction | Stock Entry — Material Issue (`MAT-STE-*`) |
| Stock levels | Bin (actual_qty per item per warehouse) |

**Required ERPNext setup:**
- One Warehouse per store (e.g. `Sterlin Glams Abuja - SG`)
- Items with codes matching `ErpNextItemCode` in the Products table
- A customer named `Walk-In Customer` (used for web orders)
- API credentials with Sales, Stock, and Inventory permissions

---

### Project Structure

```
src/SterlingLams.Web/
├── Areas/Admin/              # Admin dashboard (protected, /Admin/*)
│   ├── Controllers/          # Dashboard, Orders, Products, Inventory, Stores
│   └── Views/                # Admin UI (dark sidebar layout)
├── Controllers/              # Storefront MVC controllers
├── Models/
│   ├── Domain/               # EF Core entities
│   └── ViewModels/           # View-specific models
├── Services/
│   ├── ERPNext/              # ERPNext REST API integration
│   ├── Payment/              # Paystack / Stripe / Flutterwave
│   └── Inventory/            # Stock sync logic
├── Data/                     # EF Core DbContext + Migrations
├── Infrastructure/           # DI extensions, InventorySyncHostedService, seeder
├── Views/                    # Razor views
└── wwwroot/                  # Tailwind output (app.css), JS
```

---

### Admin Features

- **Dashboard** — today/month revenue, pending orders, low stock alerts
- **Orders** — list, detail, status update
- **Products** — create/edit/toggle active, ERPNext item code mapping
- **Inventory** — per-store tabs with live stock levels, sync-from-ERPNext button
- **Stores** — manage store locations and ERPNext warehouse mapping