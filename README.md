## SterlingLams.com — Build Notes

### Run Locally

To run locally, all you need is:

1. Install [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
2. Set `ConnectionStrings:DefaultConnection` in `appsettings.Development.json`
3. Run:

```bash
dotnet run --project src/SterlingLams.Web
```

The database is created automatically on first run.

### Configuration

Copy `appsettings.json` values to local user secrets (never commit secrets):
```bash
cd src/SterlingLams.Web
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=sterlinglams;Username=postgres;Password=YOUR_PASSWORD"
dotnet user-secrets set "Odoo:BaseUrl"    "https://your-odoo.odoo.com"
dotnet user-secrets set "Odoo:Database"   "your_odoo_db"
dotnet user-secrets set "Odoo:Username"   "admin@yourdomain.com"
dotnet user-secrets set "Odoo:ApiKey"     "your_odoo_api_key"
dotnet user-secrets set "Payment:Provider"               "Paystack"
dotnet user-secrets set "Payment:Paystack:SecretKey"     "sk_test_..."
dotnet user-secrets set "Payment:Paystack:PublicKey"     "pk_test_..."
dotnet user-secrets set "Payment:Paystack:WebhookSecret" "whsec_..."
# Optional alternatives:
dotnet user-secrets set "Payment:Stripe:SecretKey"       "sk_test_..."
dotnet user-secrets set "Payment:Stripe:PublishableKey"  "pk_test_..."
dotnet user-secrets set "Payment:Stripe:WebhookSecret"   "whsec_..."
dotnet user-secrets set "Payment:Flutterwave:SecretKey"  "FLWSECK_TEST-..."
```

### Database Migrations (EF Core)

Run these commands after installing the .NET 9 SDK:

```bash
cd src/SterlingLams.Web

# Install EF tools (once per machine)
dotnet tool install --global dotnet-ef

# Create the initial migration
dotnet ef migrations add InitialCreate --output-dir Data/Migrations

# Apply to database
dotnet ef database update
```

On startup the app will:
1. Auto-migrate the database (`MigrateAsync` in development)
2. Seed roles (Admin, Customer)
3. Seed the 3 Lagos stores (Victoria Island, Ikeja, Lekki)
4. Seed product categories (Rings, Necklaces, Earrings, etc.)

### Granting Admin Access

After registering your account, run this SQL against PostgreSQL:
```sql
INSERT INTO "AspNetUserRoles" ("UserId", "RoleId")
SELECT u."Id", r."Id"
FROM "AspNetUsers" u
CROSS JOIN "AspNetRoles" r
WHERE u."Email" = 'your@email.com'
  AND r."Name"  = 'Admin';
```

Then visit `/Admin/Dashboard`.

### Project Structure
```
src/SterlingLams.Web/
├── Areas/Admin/          # Admin dashboard (protected)
│   ├── Controllers/      # Dashboard, Orders, Products, Inventory, Stores
│   └── Views/            # Admin UI (dark sidebar layout)
├── Controllers/          # Storefront MVC controllers
├── Models/
│   ├── Domain/           # EF Core entities
│   └── ViewModels/       # View-specific models
├── Services/
│   ├── Odoo/             # Odoo JSON-RPC integration
│   ├── Payment/          # Paystack / Stripe / Flutterwave
│   └── Inventory/        # Stock sync + caching
├── Data/                 # EF Core DbContext + Migrations/
├── Infrastructure/       # DI extensions, background services, seeder
├── Views/                # Razor views (luxury Tiffany-style)
└── wwwroot/              # Tailwind output (app.css), JS
```

### Odoo Integration
- Uses JSON-RPC 2.0 (`/jsonrpc` endpoint)
- Authenticates with API key (Odoo 16+)
- Stock read from `stock.quant` per warehouse
- Sales written to `sale.order` + confirmed after payment
- Background sync every 5 minutes via `InventorySyncHostedService`
- Odoo warehouse IDs map to stores via `appsettings.json > Odoo:Stores`

### Payment Providers

Set `Payment:Provider` in appsettings/secrets:

| Provider | Key | Notes |
|---|---|---|
| `Paystack` | Default | Best for NGN, Nigerian cards |
| `Stripe` | Checkout Session flow | International cards |
| `Flutterwave` | Standard redirect | Multi-currency, Africa-wide |

Webhook endpoints:
- Paystack: `POST /webhooks/paystack` — HMAC-SHA512 validation
- Stripe: extend `WebhooksController` using `Stripe.EventUtility`
- Flutterwave: extend `WebhooksController` using HMAC-SHA256

### Tailwind CSS

```bash
npm run build:css    # one-time production build (minified)
npm run watch:css    # dev watch mode
```

Admin views use the same `app.css` — `Areas/**/*.cshtml` is included in
`tailwind.config.js` content paths.
