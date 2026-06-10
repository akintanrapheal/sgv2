# Catalog import

Imports the full product catalog (products + multi-attribute variants + images) from the legacy
WooCommerce site into the in-house DB.

## Pipeline

1. **Extract** `catalog.json` from a WordPress backup (All-in-One WP Migration `.wpress`).
   - The `.wpress` contains a `database.sql` MySQL dump (table prefix `SERVMASK_PREFIX_`).
   - `extract_catalog.py` parses `wp_posts` / `wp_postmeta` / `wp_terms*` and writes a structured
     `catalog.json`: each product with `sku`, `title`, `description`, `categories`, `images`
     (live `sterlinglams.com` URLs), and `variants[]` (each with its `attrs`, e.g. `{color, size}`).
   - Point the `DB` path in the script at the extracted `database.sql`, then `python extract_catalog.py`.

2. **Import** into the app DB:
   ```
   dotnet run -- import-catalog "tools/catalog-import/catalog.json"
   ```
   This **wipes existing products** (cascades to variants/images/inventory) and re-imports the full
   catalog. Stock is imported as **zero** — set per-branch quantities afterwards in admin/Inventory.
   Keyed on `ExternalCode` = `WC-{sku}`. Implemented by `Services/CatalogImportService.cs`.

## Notes
- ~2,021 published products, ~2,644 variants. Variant attributes: Color, Size, Alphabet,
  Measurement, Signs, Combo.
- Images reference live `https://sterlinglams.com/wp-content/uploads/...` URLs (verified resolving).
- `catalog.json` is checked in so the import is reproducible without the 7 GB backup.
