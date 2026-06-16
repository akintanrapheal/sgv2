-- Merge duplicate storefront categories into the curated "Shop by Category" tiles.
--
-- Why: the curated tiles (Necklaces, Watches, Bracelets) had images but almost no products,
-- because the bulk import filed products under parallel names (Necklaces & Pendants,
-- Strap/Bracelet Watches, Bracelets & Bangles). Clicking the tiles showed an empty page.
--
-- This script reassigns every product from each import-named duplicate into its curated tile,
-- then deactivates the now-empty duplicate so it drops out of the storefront nav.
--
-- Keyed by SLUG (not Id) so it is portable across environments and idempotent
-- (safe to run more than once). Already applied to dev DB 2026-06-16.

BEGIN;

-- product reassignments: each row = (source duplicate slug -> target curated slug)
WITH moves(src_slug, dst_slug) AS (
    VALUES
        ('necklaces-pendants', 'necklaces'),
        ('bracelets-bangles',  'bracelets'),
        ('strap-watches',      'watches'),
        ('bracelet-watches',   'watches')
)
UPDATE "Products" p
SET    "CategoryId" = dst."Id"
FROM   moves m
JOIN   "Categories" src ON src."Slug" = m.src_slug
JOIN   "Categories" dst ON dst."Slug" = m.dst_slug
WHERE  p."CategoryId" = src."Id";

-- deactivate the emptied duplicates (reversible: set IsActive = true to restore)
UPDATE "Categories"
SET    "IsActive" = false
WHERE  "Slug" IN ('necklaces-pendants', 'bracelets-bangles', 'strap-watches', 'bracelet-watches');

COMMIT;

-- sanity check (run separately):
--   SELECT c."Name", c."IsActive", count(p."Id") FILTER (WHERE p."IsActive") AS active_products
--   FROM "Categories" c LEFT JOIN "Products" p ON p."CategoryId" = c."Id"
--   WHERE c."Slug" IN ('necklaces','bracelets','watches','necklaces-pendants','bracelets-bangles','strap-watches','bracelet-watches')
--   GROUP BY c."Id" ORDER BY c."IsActive" DESC, c."Name";
