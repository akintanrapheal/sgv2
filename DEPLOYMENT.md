# Sterlin Glams — Setup, Logins & Deployment Guide

This document explains how to run, access, and host the Sterlin Glams website.

> The website is **ASP.NET Core 9 (C#/.NET)** with a **PostgreSQL** database.
> ERPNext (on Frappe Cloud) is a separate system used for POS + inventory.

---

## 1. Logins

### Website Admin
- **URL (local):** http://localhost:5000/Account/Login
- **Email:** `rapheal@sterlinglamslogistics.com`
- **Default password:** `Admin@sterlinglams1`
- ⚠️ **Change this immediately after first login** (Profile → Security).
  The default is created by the seeder on a fresh database.

### Staff accounts
Create more staff (Operations / Sales / Inventory / Social Media / custom) from
**Admin → Users → + New User**, then assign a role from the role dropdown.
Each role only sees the admin sections you grant it
(**Admin → Roles & Permissions**).

### Database (local development only)
| Field | Value |
|-------|-------|
| Host | `localhost` |
| Port | `5432` |
| Database | `sterlinglams_dev` |
| Username | `postgres` |
| Password | `postgres` |

View it with **pgAdmin 4** (installed with PostgreSQL):
`Servers → PostgreSQL 18 → Databases → sterlinglams_dev → Schemas → public → Tables`.

### ERPNext (Frappe Cloud)
- **URL:** https://sterlinglams.l.frappe.cloud
- Manage login / API keys from the Frappe Cloud dashboard.

### Source code (GitHub)
- **Repo:** https://github.com/sterlinglamslogistics-tech/sterlinglams-erpnext
- Push local commits with: `git push origin master`

---

## 2. Running locally

```bash
# 1. Make sure PostgreSQL is running
# 2. Build the Tailwind CSS (only needed after editing .cshtml/styles)
npm install
npm run build:css

# 3. Run the app
dotnet run --project src/SterlingLams.Web/SterlingLams.Web.csproj --urls http://localhost:5000
```

On first run against an empty database the app automatically seeds:
roles, the 3 stores, categories, product attributes, site settings, and the admin user.

---

## 3. Secrets & configuration

**Never put real secrets in committed files.** Configuration is layered:

- `appsettings.json` — safe defaults / placeholders (committed)
- `appsettings.Development.json` — your **local** secrets (now git-ignored)
- **Environment variables** — used in production (override everything)

Keys the app reads:

| Setting | Env-var form |
|---------|--------------|
| Database | `ConnectionStrings__DefaultConnection` |
| ERPNext base URL | `ERPNext__BaseUrl` |
| ERPNext API key | `ERPNext__ApiKey` |
| ERPNext API secret | `ERPNext__ApiSecret` |
| Paystack secret key | `Payment__Paystack__SecretKey` |
| Paystack public key | `Payment__Paystack__PublicKey` |
| Environment | `ASPNETCORE_ENVIRONMENT` = `Production` |

---

## 4. Hosting the website

The website needs a **.NET-capable host** + a **hosted PostgreSQL** database.
(It cannot run on Frappe Cloud — that is only for ERPNext.)

| Host | Notes | Rough cost |
|------|-------|-----------|
| **Render.com** ⭐ recommended | Free Postgres tier, deploys from GitHub, auto HTTPS | Free–$7/mo |
| Railway.app | .NET + Postgres together, simple | ~$5/mo |
| DigitalOcean App Platform | Popular, reliable | ~$12/mo |
| Azure App Service | Microsoft-native for .NET | ~$13/mo+ |
| VPS (Hetzner/Contabo) | Cheapest, full control, most work | ~$5/mo |

### Recommended: Render.com

1. Push code to GitHub: `git push origin master`
2. [render.com](https://render.com) → sign up with GitHub.
3. **New → PostgreSQL** → name `sterlinglams` → copy the **Internal Connection String**.
4. **New → Web Service** → connect the GitHub repo.
   - Build: `dotnet publish src/SterlingLams.Web/SterlingLams.Web.csproj -c Release -o out`
   - Start: `dotnet out/SterlingLams.Web.dll`
   - (Or use a Dockerfile — ask the dev to add one.)
5. Add the **environment variables** from section 3 (use the Postgres string from step 3,
   your ERPNext keys, and your **LIVE** Paystack keys).
6. Deploy. You get a URL like `sterlinglams.onrender.com`.
7. **Settings → Custom Domains** → add `sterlinglams.com` and follow the DNS steps.

---

## 5. Going-live checklist

- [ ] Push code to GitHub (backup)
- [ ] Change the admin password
- [ ] Switch Paystack from **test** keys to **LIVE** keys
- [ ] Set all secrets as environment variables on the host (not in files)
- [ ] Point your domain at the host and confirm HTTPS works
- [ ] Place one real test order end-to-end (pay → order confirmed → ERPNext invoice created)
- [ ] Re-import / set product stock per store from **Admin → Inventory**

---

## 6. Security notes

- The default admin password is in the seed code — change it after first login.
- `appsettings.Development.json` is git-ignored so local secrets aren't pushed.
- If the ERPNext API keys were ever committed to git history, **rotate them** in
  ERPNext (revoke the old key, generate a new one) and update your config.
- Every admin action is recorded in **Admin → Audit Log** (who, what, when, IP).
