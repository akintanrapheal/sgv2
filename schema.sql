-- ══════════════════════════════════════════════════════════════════════════════
-- SterlingLams.com — Manual PostgreSQL Schema
-- Use this ONLY if you cannot run `dotnet ef migrations add InitialCreate`.
-- If using EF Core with EnsureCreated() in Development, this file is NOT needed.
--
-- How to apply:
--   psql -h localhost -U postgres -d sterlinglams -f schema.sql
-- ══════════════════════════════════════════════════════════════════════════════

-- ─── ASP.NET Core Identity ────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS "AspNetRoles" (
    "Id"               TEXT NOT NULL PRIMARY KEY,
    "Name"             TEXT,
    "NormalizedName"   TEXT,
    "ConcurrencyStamp" TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS "RoleNameIndex" ON "AspNetRoles" ("NormalizedName");

CREATE TABLE IF NOT EXISTS "AspNetUsers" (
    "Id"                   TEXT NOT NULL PRIMARY KEY,
    "UserName"             TEXT,
    "NormalizedUserName"   TEXT,
    "Email"                TEXT,
    "NormalizedEmail"      TEXT,
    "EmailConfirmed"       BOOLEAN NOT NULL,
    "PasswordHash"         TEXT,
    "SecurityStamp"        TEXT,
    "ConcurrencyStamp"     TEXT,
    "PhoneNumber"          TEXT,
    "PhoneNumberConfirmed" BOOLEAN NOT NULL,
    "TwoFactorEnabled"     BOOLEAN NOT NULL,
    "LockoutEnd"           TIMESTAMPTZ,
    "LockoutEnabled"       BOOLEAN NOT NULL,
    "AccessFailedCount"    INTEGER NOT NULL,
    -- ApplicationUser custom fields
    "FirstName"            TEXT,
    "LastName"             TEXT,
    "PhoneNumberAlt"       TEXT,
    "DateOfBirth"          DATE,
    "IsActive"             BOOLEAN NOT NULL DEFAULT TRUE,
    "OdooPartnerId"        INTEGER,
    "CreatedAt"            TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE UNIQUE INDEX IF NOT EXISTS "UserNameIndex"  ON "AspNetUsers" ("NormalizedUserName");
CREATE INDEX        IF NOT EXISTS "EmailIndex"     ON "AspNetUsers" ("NormalizedEmail");

CREATE TABLE IF NOT EXISTS "AspNetUserRoles" (
    "UserId" TEXT NOT NULL REFERENCES "AspNetUsers"("Id") ON DELETE CASCADE,
    "RoleId" TEXT NOT NULL REFERENCES "AspNetRoles"("Id") ON DELETE CASCADE,
    PRIMARY KEY ("UserId", "RoleId")
);

CREATE TABLE IF NOT EXISTS "AspNetUserClaims" (
    "Id"         SERIAL PRIMARY KEY,
    "UserId"     TEXT NOT NULL REFERENCES "AspNetUsers"("Id") ON DELETE CASCADE,
    "ClaimType"  TEXT,
    "ClaimValue" TEXT
);

CREATE TABLE IF NOT EXISTS "AspNetUserLogins" (
    "LoginProvider"       TEXT NOT NULL,
    "ProviderKey"         TEXT NOT NULL,
    "ProviderDisplayName" TEXT,
    "UserId"              TEXT NOT NULL REFERENCES "AspNetUsers"("Id") ON DELETE CASCADE,
    PRIMARY KEY ("LoginProvider", "ProviderKey")
);

CREATE TABLE IF NOT EXISTS "AspNetUserTokens" (
    "UserId"        TEXT NOT NULL REFERENCES "AspNetUsers"("Id") ON DELETE CASCADE,
    "LoginProvider" TEXT NOT NULL,
    "Name"          TEXT NOT NULL,
    "Value"         TEXT,
    PRIMARY KEY ("UserId", "LoginProvider", "Name")
);

CREATE TABLE IF NOT EXISTS "AspNetRoleClaims" (
    "Id"         SERIAL PRIMARY KEY,
    "RoleId"     TEXT NOT NULL REFERENCES "AspNetRoles"("Id") ON DELETE CASCADE,
    "ClaimType"  TEXT,
    "ClaimValue" TEXT
);

-- ─── Categories ──────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS "Categories" (
    "Id"          SERIAL PRIMARY KEY,
    "Name"        TEXT NOT NULL,
    "Slug"        TEXT NOT NULL UNIQUE,
    "Description" TEXT,
    "ImageUrl"    TEXT,
    "ParentId"    INTEGER REFERENCES "Categories"("Id"),
    "SortOrder"   INTEGER NOT NULL DEFAULT 0,
    "IsActive"    BOOLEAN NOT NULL DEFAULT TRUE
);

-- ─── Products ────────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS "Products" (
    "Id"               SERIAL PRIMARY KEY,
    "OdooProductId"    INTEGER NOT NULL UNIQUE,
    "Name"             TEXT NOT NULL,
    "Slug"             TEXT NOT NULL UNIQUE,
    "Description"      TEXT,
    "ShortDescription" TEXT,
    "Price"            NUMERIC(18,2) NOT NULL,
    "Currency"         TEXT NOT NULL DEFAULT 'NGN',
    "Sku"              TEXT,
    "Barcode"          TEXT,
    "Material"         TEXT,
    "Metal"            TEXT,
    "GemstoneType"     TEXT,
    "Carat"            TEXT,
    "Weight"           TEXT,
    "IsActive"         BOOLEAN NOT NULL DEFAULT TRUE,
    "IsFeatured"       BOOLEAN NOT NULL DEFAULT FALSE,
    "IsNewArrival"     BOOLEAN NOT NULL DEFAULT FALSE,
    "CreatedAt"        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    "UpdatedAt"        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    "CategoryId"       INTEGER NOT NULL REFERENCES "Categories"("Id")
);

-- ─── ProductImages ────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS "ProductImages" (
    "Id"        SERIAL PRIMARY KEY,
    "ProductId" INTEGER NOT NULL REFERENCES "Products"("Id") ON DELETE CASCADE,
    "Url"       TEXT NOT NULL,
    "AltText"   TEXT,
    "SortOrder" INTEGER NOT NULL DEFAULT 0,
    "IsPrimary" BOOLEAN NOT NULL DEFAULT FALSE
);

-- ─── ProductVariants ─────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS "ProductVariants" (
    "Id"               SERIAL PRIMARY KEY,
    "ProductId"        INTEGER NOT NULL REFERENCES "Products"("Id") ON DELETE CASCADE,
    "Name"             TEXT NOT NULL,
    "Sku"              TEXT,
    "PriceAdjustment"  NUMERIC(18,2) NOT NULL DEFAULT 0,
    "IsActive"         BOOLEAN NOT NULL DEFAULT TRUE,
    "OdooVariantId"    INTEGER
);

-- ─── ProductTags ─────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS "ProductTags" (
    "Id"   SERIAL PRIMARY KEY,
    "Name" TEXT NOT NULL,
    "Slug" TEXT NOT NULL UNIQUE
);

CREATE TABLE IF NOT EXISTS "ProductProductTag" (
    "ProductsId" INTEGER NOT NULL REFERENCES "Products"("Id") ON DELETE CASCADE,
    "TagsId"     INTEGER NOT NULL REFERENCES "ProductTags"("Id") ON DELETE CASCADE,
    PRIMARY KEY ("ProductsId", "TagsId")
);

-- ─── Stores ──────────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS "Stores" (
    "Id"               SERIAL PRIMARY KEY,
    "OdooWarehouseId"  INTEGER NOT NULL UNIQUE,
    "Name"             TEXT NOT NULL,
    "Slug"             TEXT NOT NULL UNIQUE,
    "Address"          TEXT NOT NULL,
    "City"             TEXT NOT NULL,
    "State"            TEXT NOT NULL DEFAULT 'Lagos',
    "Country"          TEXT NOT NULL DEFAULT 'Nigeria',
    "Phone"            TEXT,
    "Email"            TEXT,
    "OpeningHours"     TEXT,
    "Latitude"         DOUBLE PRECISION,
    "Longitude"        DOUBLE PRECISION,
    "IsActive"         BOOLEAN NOT NULL DEFAULT TRUE
);

-- ─── StoreInventory ──────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS "StoreInventories" (
    "Id"              SERIAL PRIMARY KEY,
    "ProductId"       INTEGER NOT NULL REFERENCES "Products"("Id") ON DELETE CASCADE,
    "StoreId"         INTEGER NOT NULL REFERENCES "Stores"("Id"),
    "QuantityOnHand"  INTEGER NOT NULL DEFAULT 0,
    "QuantityReserved" INTEGER NOT NULL DEFAULT 0,
    "LastSyncedAt"    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE ("ProductId", "StoreId")
);

-- ─── Addresses ───────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS "Addresses" (
    "Id"         SERIAL PRIMARY KEY,
    "UserId"     TEXT NOT NULL REFERENCES "AspNetUsers"("Id") ON DELETE CASCADE,
    "Label"      TEXT NOT NULL DEFAULT 'Home',
    "Line1"      TEXT NOT NULL,
    "Line2"      TEXT,
    "City"       TEXT NOT NULL,
    "State"      TEXT NOT NULL,
    "Country"    TEXT NOT NULL DEFAULT 'Nigeria',
    "PostalCode" TEXT,
    "IsDefault"  BOOLEAN NOT NULL DEFAULT FALSE
);

-- ─── Orders ──────────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS "Orders" (
    "Id"                SERIAL PRIMARY KEY,
    "OrderNumber"       TEXT NOT NULL UNIQUE,
    "OdooSaleOrderId"   INTEGER,
    "UserId"            TEXT NOT NULL REFERENCES "AspNetUsers"("Id"),
    "Status"            INTEGER NOT NULL DEFAULT 0,
    "FulfillmentType"   INTEGER NOT NULL DEFAULT 0,
    "PickupStoreId"     INTEGER REFERENCES "Stores"("Id"),
    "DeliveryAddressId" INTEGER REFERENCES "Addresses"("Id"),
    "Subtotal"          NUMERIC(18,2) NOT NULL,
    "DeliveryFee"       NUMERIC(18,2) NOT NULL DEFAULT 0,
    "Tax"               NUMERIC(18,2) NOT NULL DEFAULT 0,
    "Total"             NUMERIC(18,2) NOT NULL,
    "Currency"          TEXT NOT NULL DEFAULT 'NGN',
    "PaymentReference"  TEXT,
    "PaymentProvider"   TEXT,
    "IsPaid"            BOOLEAN NOT NULL DEFAULT FALSE,
    "PaidAt"            TIMESTAMPTZ,
    "Notes"             TEXT,
    "CreatedAt"         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    "UpdatedAt"         TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ─── OrderItems ──────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS "OrderItems" (
    "Id"          SERIAL PRIMARY KEY,
    "OrderId"     INTEGER NOT NULL REFERENCES "Orders"("Id") ON DELETE CASCADE,
    "ProductId"   INTEGER REFERENCES "Products"("Id"),
    "VariantId"   INTEGER REFERENCES "ProductVariants"("Id"),
    "ProductName" TEXT NOT NULL,
    "VariantName" TEXT,
    "Quantity"    INTEGER NOT NULL,
    "UnitPrice"   NUMERIC(18,2) NOT NULL
);

-- ─── WishlistItems ────────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS "WishlistItems" (
    "Id"        SERIAL PRIMARY KEY,
    "UserId"    TEXT NOT NULL REFERENCES "AspNetUsers"("Id") ON DELETE CASCADE,
    "ProductId" INTEGER NOT NULL REFERENCES "Products"("Id") ON DELETE CASCADE,
    "AddedAt"   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE ("UserId", "ProductId")
);

-- ─── EF Migrations history (required if you later add migrations) ─────────────

CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId"    TEXT NOT NULL PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);

-- ══════════════════════════════════════════════════════════════════════════════
-- Done. Run `dotnet ef migrations add InitialCreate` later to take over from here.
-- ══════════════════════════════════════════════════════════════════════════════
