using System.Text.RegularExpressions;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic.FileIO;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Admin.Controllers;

/// <summary>
/// One-time importer for products exported from the old WooCommerce site (the CSV produced during the
/// launch migration: ID, SKU, Title, Status, Type, Categories, Regular/Sale/Price, Stock, Featured
/// Image, Gallery Images, URL). Creates a simple product per row — name, SKU, price/sale, category
/// (get-or-created), and images re-uploaded to Cloudinary — and is IDEMPOTENT: any row whose SKU already
/// exists in the store (as a product or variant SKU/barcode) is skipped, so it's safe to re-run and safe
/// on prod whatever is already there. Apply works in bounded batches and reports how many remain, so the
/// page keeps calling it until done (no request timeout on hundreds of image uploads).
/// Stock is NOT set here — imported items start at 0 and the owner enters opening stock per branch.
/// Owner / full-admin only. Dry-run Preview first, then Apply.
/// </summary>
public class ProductImportController : AdminBaseController
{
    protected override string? Section => "ProductImport"; // grantable; also guarded to full-access below

    private const int Batch = 12;        // products created per Apply call (bounds image-upload time)
    private const int MaxGallery = 4;    // gallery images imported per product (plus the featured one)

    private readonly ApplicationDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<ProductImportController> _log;

    public ProductImportController(ApplicationDbContext db, IConfiguration config, ILogger<ProductImportController> log)
    {
        _db = db; _config = config; _log = log;
    }

    public IActionResult Index()
    {
        ViewData["Title"] = "Import products";
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(60_000_000)]
    public Task<IActionResult> Preview(IFormFile? file) => RunAsync(file, apply: false);

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(60_000_000)]
    public Task<IActionResult> Apply(IFormFile? file) => RunAsync(file, apply: true);

    private async Task<IActionResult> RunAsync(IFormFile? file, bool apply)
    {
        if (file == null || file.Length == 0)
            return Json(new { ok = false, error = "Please choose the products CSV file." });

        List<Dictionary<string, string>> rows;
        try { await using var s = file.OpenReadStream(); rows = ParseCsv(s); }
        catch (Exception ex) { return Json(new { ok = false, error = "Couldn't read the CSV: " + ex.Message }); }

        // Codes already in the store (product + variant SKU/barcode) → the idempotent skip set.
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in await _db.Products.Where(p => p.Sku != null).Select(p => p.Sku!).ToListAsync()) existing.Add(v.Trim());
        foreach (var v in await _db.Products.Where(p => p.Barcode != null).Select(p => p.Barcode!).ToListAsync()) existing.Add(v.Trim());
        foreach (var v in await _db.ProductVariants.Where(p => p.Sku != null).Select(p => p.Sku!).ToListAsync()) existing.Add(v.Trim());
        foreach (var v in await _db.ProductVariants.Where(p => p.Barcode != null).Select(p => p.Barcode!).ToListAsync()) existing.Add(v.Trim());
        existing.Remove("");

        var slugs = new HashSet<string>(await _db.Products.Select(p => p.Slug).ToListAsync(), StringComparer.OrdinalIgnoreCase);
        var codes = new HashSet<string>(await _db.Products.Where(p => p.ExternalCode != "").Select(p => p.ExternalCode).ToListAsync(), StringComparer.OrdinalIgnoreCase);
        var cats = (await _db.Categories.ToListAsync())
            .GroupBy(c => c.Name.Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());

        // Attributes + values → get-or-create caches (for variable products' options, e.g. Colour/Size).
        var attrsList = await _db.ProductAttributes.Include(a => a.Values).ToListAsync();
        var attrByName = attrsList.ToDictionary(a => a.Name.Trim().ToLowerInvariant());
        var attrSlugs = new HashSet<string>(attrsList.Select(a => a.Slug), StringComparer.OrdinalIgnoreCase);
        var valueCache = new Dictionary<string, ProductAttributeValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in attrsList)
            foreach (var av in a.Values)
                valueCache[a.Name.Trim().ToLowerInvariant() + "||" + av.Value.Trim().ToLowerInvariant()] = av;

        // Get-or-create one attribute value (e.g. Colour=Gold), reusing tracked instances across products
        // so the same option is never duplicated. New attributes/values persist on the next SaveChanges.
        ProductAttributeValue GetOrCreateValue(string label, string value)
        {
            label = NormAttrLabel(label); value = value.Trim();
            var alk = label.ToLowerInvariant();
            if (!attrByName.TryGetValue(alk, out var attr))
            {
                attr = new ProductAttribute { Name = label, Slug = UniqueSlug(Slugify(label), attrSlugs), IsActive = true };
                attrSlugs.Add(attr.Slug);
                _db.ProductAttributes.Add(attr);
                attrByName[alk] = attr;
            }
            var vk = alk + "||" + value.ToLowerInvariant();
            if (!valueCache.TryGetValue(vk, out var pav))
            {
                pav = new ProductAttributeValue { Attribute = attr, Value = value };
                attr.Values.Add(pav);
                valueCache[vk] = pav;
            }
            return pav;
        }

        // Variation rows grouped by their parent product's SKU (parents carry the SKU; variants add the option).
        var variationsByParent = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            if (!string.Equals(Val(r, "Type"), "product_variation", StringComparison.OrdinalIgnoreCase)) continue;
            var psku = Val(r, "Parent SKU");
            if (psku.Length == 0) continue;
            if (!variationsByParent.TryGetValue(psku, out var list)) variationsByParent[psku] = list = new();
            list.Add(r);
        }
        var variantRows = variationsByParent.Sum(kv => kv.Value.Count);

        int totalRows = rows.Count, candidates = 0, skippedExisting = 0, skippedNoSku = 0,
            skippedNoTitle = 0, created = 0, newCats = 0, imgUploaded = 0, variantsCreated = 0;
        var samples = new List<object>();
        var cloud = GetCloudinary();

        foreach (var r in rows)
        {
            string G(params string[] keys)
            {
                foreach (var k in keys)
                    if (r.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) return v.Trim();
                return "";
            }

            if (string.Equals(G("Type"), "product_variation", StringComparison.OrdinalIgnoreCase)) continue; // handled via variationsByParent
            var title = G("Title");
            if (title.Length == 0) { skippedNoTitle++; continue; }
            var sku = G("SKU");
            if (sku.Length == 0) { skippedNoSku++; continue; }
            if (existing.Contains(sku)) { skippedExisting++; continue; }

            candidates++;
            var catName = G("Categories").Split('|')[0].Trim();
            if (samples.Count < 12)
                samples.Add(new { sku, title, category = catName, price = G("Price", "Regular Price"), status = G("Status") });

            if (!apply) continue;
            if (created >= Batch) continue;   // leave the rest for the next Apply call

            // ── Create the product ──
            var price = ParseMoney(G("Price", "Regular Price"));
            var regular = ParseMoney(G("Regular Price"));
            if (price == 0m && regular > 0m) price = regular;
            var sale = ParseMoney(G("Sale Price"));
            decimal? salePrice = (sale > 0m && sale < price) ? sale : (decimal?)null;

            // Category: get-or-create by name (flat — storefront grouping is by StoreMenu, not ParentId).
            int categoryId;
            var key = (catName.Length == 0 ? "uncategorised" : catName).ToLowerInvariant();
            if (cats.TryGetValue(key, out var cat)) categoryId = cat.Id;
            else
            {
                var display = catName.Length == 0 ? "Uncategorised" : catName;
                var newCat = new Category { Name = display, Slug = UniqueSlug(Slugify(display), slugs), IsActive = true };
                _db.Categories.Add(newCat);
                await _db.SaveChangesAsync();
                cats[key] = newCat; categoryId = newCat.Id; newCats++;
                slugs.Add(newCat.Slug);
            }

            var slug = UniqueSlug(Slugify(title), slugs); slugs.Add(slug);
            var extCode = UniqueCode("WP-" + G("ID"), codes); codes.Add(extCode);
            var vrows = variationsByParent.GetValueOrDefault(sku);
            var isVariable = vrows != null && vrows.Count > 0;

            var product = new Product
            {
                ExternalCode = extCode,
                Name = title,
                Slug = slug,
                Price = price,
                SalePrice = salePrice,
                Currency = "NGN",
                Sku = sku,
                ProductType = isVariable ? "variable" : "simple",
                CategoryId = categoryId,
                IsActive = true,
                TrackStock = true,
                LowStockThreshold = 3,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };

            // Images: featured first (primary), then up to MaxGallery gallery images. Re-hosted on
            // Cloudinary (fetched from the old URL); falls back to the original URL if upload fails.
            var imgUrls = new List<string>();
            var feat = G("Featured Image");
            if (feat.Length > 0) imgUrls.Add(feat);
            foreach (var g in G("Gallery Images").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (imgUrls.Count > MaxGallery) break;
                if (!imgUrls.Contains(g)) imgUrls.Add(g);
            }
            int sort = 0;
            foreach (var u in imgUrls)
            {
                var hosted = await UploadImageAsync(cloud, u);
                if (hosted == null) continue;
                if (!hosted.Equals(u, StringComparison.OrdinalIgnoreCase)) imgUploaded++;
                product.Images.Add(new ProductImage { Url = hosted, IsPrimary = sort == 0, SortOrder = sort });
                sort++;
            }

            // Variable product: build a variant per variation row, with its option value(s) and price.
            // Old variations carry no SKU of their own (the SKU is on the parent), so variant SKU stays null.
            if (isVariable)
            {
                foreach (var vr in vrows!)
                {
                    var options = ParseOptions(Val(vr, "Options"));
                    if (options.Count == 0) continue;
                    var avs = options.Select(o => GetOrCreateValue(o.label, o.value)).ToList();
                    var vreg = ParseMoney(Val(vr, "Regular Price"));
                    if (vreg == 0m) vreg = ParseMoney(Val(vr, "Price"));
                    var vsale = ParseMoney(Val(vr, "Sale Price"));
                    var variant = new ProductVariant
                    {
                        Name = string.Join(" / ", avs.Select(a => a.Value)),
                        Price = vreg > 0m ? vreg : (decimal?)null,
                        SalePrice = (vsale > 0m && vsale < vreg) ? vsale : (decimal?)null,
                        StockQuantity = 0,   // opening stock is entered per branch, like simple products
                        IsActive = true,
                    };
                    foreach (var av in avs) variant.AttributeValues.Add(av);
                    product.Variants.Add(variant);
                    variantsCreated++;
                }
            }

            _db.Products.Add(product);
            await _db.SaveChangesAsync();   // per-product commit → resumable if the batch is cut short
            existing.Add(sku);
            created++;
        }

        if (apply && created > 0)
            await LogAsync("Import", "Product", null,
                $"Imported {created} product(s) from CSV ({variantsCreated} variants; skipped {skippedExisting} existing; {newCats} new categories; {imgUploaded} images hosted).");

        var remaining = apply ? Math.Max(0, candidates - created) : candidates;
        return Json(new
        {
            ok = true,
            applied = apply,
            totalRows,
            candidates,
            created,
            remaining,
            newCategories = newCats,
            imagesHosted = imgUploaded,
            variantsCreated,
            variantRows,
            skippedExisting,
            skippedNoSku,
            skippedNoTitle,
            samples,
        });
    }

    private static readonly Dictionary<string, string> AttrSynonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["color"] = "Colour", ["colour"] = "Colour", ["size"] = "Size", ["length"] = "Length",
    };

    private static string NormAttrLabel(string label)
    {
        label = (label ?? "").Trim();
        return AttrSynonyms.TryGetValue(label, out var norm) ? norm : label;
    }

    // "Colour=Gold; Size=7" → [(Colour,Gold),(Size,7)]
    private static List<(string label, string value)> ParseOptions(string s)
    {
        var list = new List<(string, string)>();
        foreach (var part in (s ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var label = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            if (label.Length > 0 && value.Length > 0) list.Add((label, value));
        }
        return list;
    }

    private static string Val(Dictionary<string, string> r, string key)
        => r.TryGetValue(key, out var v) && v != null ? v.Trim() : "";

    private Cloudinary? GetCloudinary()
    {
        var cn = _config["Cloudinary:CloudName"]; var ak = _config["Cloudinary:ApiKey"]; var asx = _config["Cloudinary:ApiSecret"];
        if (string.IsNullOrWhiteSpace(cn) || string.IsNullOrWhiteSpace(ak) || string.IsNullOrWhiteSpace(asx)) return null;
        return new Cloudinary(new Account(cn, ak, asx)) { Api = { Secure = true } };
    }

    // Upload a remote image URL to Cloudinary (server-side fetch). Returns the hosted URL, or the
    // original URL if Cloudinary isn't configured / the upload fails (so the image still shows).
    private async Task<string?> UploadImageAsync(Cloudinary? cloud, string url)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0) return null;
        if (cloud == null) return url;
        try
        {
            var res = await cloud.UploadAsync(new ImageUploadParams
            {
                File = new FileDescription(url),
                Folder = "sterlinglams/products",
                PublicId = Guid.NewGuid().ToString("N"),
                UniqueFilename = false,
                Overwrite = false,
            });
            if (res.StatusCode == System.Net.HttpStatusCode.OK && res.SecureUrl != null)
                return res.SecureUrl.ToString();
            _log.LogWarning("Product import: Cloudinary upload failed for {Url}: {Status}", url, res.Error?.Message);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Product import: image upload threw for {Url}", url); }
        return url; // fall back to the original URL
    }

    private static decimal ParseMoney(string s)
    {
        s = new string((s ?? "").Where(c => char.IsDigit(c) || c == '.').ToArray());
        return decimal.TryParse(s, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0m;
    }

    private static string Slugify(string s)
    {
        var slug = Regex.Replace((s ?? "").ToLowerInvariant().Trim(), @"[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "product" : slug;
    }

    private static string UniqueSlug(string baseSlug, HashSet<string> taken)
    {
        if (!taken.Contains(baseSlug)) return baseSlug;
        var n = 2; string s;
        do { s = $"{baseSlug}-{n++}"; } while (taken.Contains(s));
        return s;
    }

    private static string UniqueCode(string baseCode, HashSet<string> taken)
    {
        if (baseCode.Length == 0) baseCode = "WP-" + Guid.NewGuid().ToString("N")[..8];
        if (!taken.Contains(baseCode)) return baseCode;
        var n = 2; string s;
        do { s = $"{baseCode}-{n++}"; } while (taken.Contains(s));
        return s;
    }

    // Robust CSV parse (quoted fields, embedded commas/newlines) — same approach as CustomerImport.
    private static List<Dictionary<string, string>> ParseCsv(Stream stream)
    {
        var rows = new List<Dictionary<string, string>>();
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        using var parser = new TextFieldParser(reader) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true };
        parser.SetDelimiters(",");
        if (parser.EndOfData) return rows;

        var headers = (parser.ReadFields() ?? Array.Empty<string>())
            .Select(h => h.Trim().TrimStart('﻿')).ToArray();

        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields() ?? Array.Empty<string>();
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headers.Length && i < fields.Length; i++)
                row[headers[i]] = fields[i];
            rows.Add(row);
        }
        return rows;
    }
}
