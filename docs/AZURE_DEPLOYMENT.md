# Azure Deployment Guide

> Deploy **Sterlin Glams** (ASP.NET Core 9 + PostgreSQL) to **Azure App Service (Linux)**
> with CI/CD from GitHub. Developed by **Dev Rapheal**.

The pipeline lives in [`.github/workflows/azure-deploy.yml`](../.github/workflows/azure-deploy.yml):
on every push to `main` it builds the Tailwind CSS, publishes the app, **applies database
migrations as a gated step** (the app itself does not auto-migrate — see OP-12), then deploys.

---

## 0. What you'll create

| Azure resource | Purpose |
|---|---|
| Resource group | container for everything below |
| Azure Database for PostgreSQL — Flexible Server | the production database |
| App Service plan (Linux, B1+) | compute for the web app |
| App Service (Web App, .NET 9) | runs the site |

You need: an Azure subscription, the [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli)
(`az`) logged in (`az login`), and admin access to the GitHub repo.

Pick names/values once and reuse them:

```bash
RG=sterlinglams-rg
LOCATION=uksouth                 # or your nearest region
PG=sterlinglams-pg              # PostgreSQL server name (globally unique)
PGADMIN=sladmin
PGPASS='<a-strong-password>'    # save this
APP=sterlinglams               # App Service name (globally unique -> https://APP.azurewebsites.net)
PLAN=sterlinglams-plan
```

---

## 1. Create the database

```bash
az group create -n $RG -l $LOCATION

az postgres flexible-server create \
  --resource-group $RG --name $PG --location $LOCATION \
  --admin-user $PGADMIN --admin-password "$PGPASS" \
  --tier Burstable --sku-name Standard_B1ms \
  --version 16 --storage-size 32 --public-access 0.0.0.0

az postgres flexible-server db create \
  --resource-group $RG --server-name $PG --database-name sterlinglams
```

- `--public-access 0.0.0.0` turns on **"Allow public access from Azure services"** so App Service
  can reach it. The GitHub Actions runner that applies migrations is **not** an Azure service, so
  also add a firewall rule it can use (see step 4, "Migrations & the DB firewall").

The connection string the app uses (note **SSL is required**):

```
Host=<PG>.postgres.database.azure.com;Port=5432;Database=sterlinglams;Username=<PGADMIN>;Password=<PGPASS>;SSL Mode=Require;Trust Server Certificate=true
```

---

## 2. Create the App Service

```bash
az appservice plan create -g $RG -n $PLAN --is-linux --sku B1

az webapp create -g $RG -p $PLAN -n $APP --runtime "DOTNETCORE:9.0"
```

Then set the app configuration (**these are App Service settings, not in the repo**):

```bash
az webapp config appsettings set -g $RG -n $APP --settings \
  ASPNETCORE_ENVIRONMENT=Production \
  ConnectionStrings__DefaultConnection="Host=$PG.postgres.database.azure.com;Port=5432;Database=sterlinglams;Username=$PGADMIN;Password=$PGPASS;SSL Mode=Require;Trust Server Certificate=true" \
  Database__AutoMigrate=false \
  AllowedHosts="$APP.azurewebsites.net;sterlinglams.com;www.sterlinglams.com" \
  Payment__Paystack__SecretKey="<LIVE_or_TEST_secret>" \
  Payment__Paystack__PublicKey="<LIVE_or_TEST_public>" \
  Payment__Paystack__WebhookSecret="<paystack_webhook_secret>" \
  Email__Host="<smtp host>" Email__Port="587" \
  Email__Username="<smtp user>" Email__Password="<smtp pass>" \
  Email__FromAddress="no-reply@sterlinglams.com"
```

Notes:
- **`Database__AutoMigrate=false`** is intentional. Migrations are applied by the pipeline before
  deploy; the app refuses to start with unapplied migrations rather than silently migrating.
- `AllowedHosts` must include whatever host you browse — the free `*.azurewebsites.net` host and/or
  your custom domain. (Leaving it wrong gives a 400 "Bad Request".)
- Data-protection keys (auth/antiforgery cookies) persist automatically: they default to
  `App_Data/dp-keys` under `/home/site/wwwroot`, and `/home` is persistent on App Service.
  If you scale to **multiple instances**, point `DataProtection__KeysPath` at a shared path.

---

## 3. Wire up GitHub → Azure

1. **Get the publish profile:**
   ```bash
   az webapp deployment list-publishing-profiles -g $RG -n $APP --xml
   ```
   Copy the entire XML.

2. In GitHub: **repo → Settings → Secrets and variables → Actions → New repository secret**, add:
   | Secret | Value |
   |---|---|
   | `AZURE_WEBAPP_PUBLISH_PROFILE` | the XML from above |
   | `PROD_DB_CONNECTION` | the Npgsql connection string from step 1 |

3. Edit [`.github/workflows/azure-deploy.yml`](../.github/workflows/azure-deploy.yml) and set
   `AZURE_WEBAPP_NAME` to your `$APP` name.

---

## 4. Deploy

Push to `main` (or run the workflow manually from the **Actions** tab):

```bash
git push sgv2 main
```

The workflow will: build CSS → `dotnet publish` → apply migrations → deploy. Watch it under
the repo's **Actions** tab.

### Migrations & the DB firewall
The migration step runs from the GitHub-hosted runner, whose IP is dynamic. Options, easiest → safest:
- **Quick:** add a temporary firewall rule allowing all IPs (SSL is still required), then tighten later:
  ```bash
  az postgres flexible-server firewall-rule create -g $RG -n $PG \
    --rule-name github-actions --start-ip-address 0.0.0.0 --end-ip-address 255.255.255.255
  ```
- **Safer:** run migrations yourself from **Azure Cloud Shell** instead of the runner — delete the
  "Apply EF Core migrations" step from the workflow and run, against the same DB:
  ```bash
  dotnet tool install --global dotnet-ef --version 9.*
  dotnet ef database update --project src/SterlingLams.Web/SterlingLams.Web.csproj \
    --connection "<PROD_DB_CONNECTION>"
  ```

---

## 5. Test it live

1. Open `https://<APP>.azurewebsites.net` — the storefront should load.
2. **Log in to admin** at `/Account/Login` with the seeded admin
   (`rapheal@sterlinglamslogistics.com` / `Admin@sterlinglams1`) and **change the password immediately**.
3. Smoke-test: browse a category, add to bag, reach checkout; open `/Admin`, `/Inventory`, `/Till`.
4. If you see a 400, fix `AllowedHosts`. If it won't start, check **App Service → Log stream**
   (a clear "pending migrations" message means the migration step didn't run against this DB).

### Apply the one-off data fix
The category-merge cleanup is a one-time data change. Run it against the prod DB once:
```bash
psql "<PROD_DB_CONNECTION as libpq URL>" -f tools/data-fixes/merge-duplicate-categories.sql
```

---

## 6. Go-live checklist

- [ ] Admin password changed from the seeded default.
- [ ] **Paystack keys are LIVE keys** (the test keys in git history must be rotated in the Paystack
      dashboard — they are compromised; see OP-1).
- [ ] SMTP configured (`Email__*`) so order/account emails actually send.
- [ ] Custom domain + managed TLS bound (`az webapp config hostname add` + App Service Managed
      Certificate), and the domain added to `AllowedHosts`.
- [ ] Paystack webhook pointed at `https://<your-domain>/webhooks/paystack` (or the configured route).
- [ ] DB firewall tightened (remove the broad rule once migrations run, or switch to Cloud Shell).
- [ ] Category data fix applied (step 5).
- [ ] App Service → **Backups** / PostgreSQL automated backups confirmed.

---

## Rollback

- **Code:** App Service → **Deployment Center** keeps history; or re-run the workflow on an older commit.
- **Database:** PostgreSQL Flexible Server supports point-in-time restore. Migrations are forward-only —
  test a fresh `dotnet ef database update` against a staging copy before any risky migration.
