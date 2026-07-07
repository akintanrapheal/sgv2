-- Move products into the "Bracelet" category (slug: mens-bracelets) by SKU.
-- Run against the PRODUCTION database (Render Postgres) once you have your list.
-- Add/remove SKUs in the IN (...) list below — that's the only line you edit.
--
-- A moved product appears on the storefront under the "Bracelet" menu as long as it is
-- Active (and, if you've enabled Settings → "hide out of stock", has stock in a store).

UPDATE "Products"
SET "CategoryId" = (SELECT "Id" FROM "Categories" WHERE "Slug" = 'mens-bracelets')
WHERE "Sku" IN (
    'SGK02', 'SGK29', 'SGK18', 'SGK13', 'SGK32',
    'SGK21', 'SGK12', 'SGK31', 'SGK01', 'SGK28'
    -- add more SKUs here, comma-separated
);

-- NOTE: this is a manual, ad-hoc tool — it does NOT run automatically. The initial 10 SKUs were
-- already filed by an earlier deploy; use this only if you want to bulk-move more products by SKU.

-- Confirm what's now in the Bracelet category:
SELECT p."Sku", p."Name", c."Name" AS category, p."IsActive"
FROM "Products" p
JOIN "Categories" c ON c."Id" = p."CategoryId"
WHERE c."Slug" = 'mens-bracelets'
ORDER BY p."Name";
