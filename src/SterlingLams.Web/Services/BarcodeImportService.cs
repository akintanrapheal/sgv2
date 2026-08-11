using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

public class BarcodeImportResult
{
    public int RowsRead { get; set; }
    public int ProductsMatched { get; set; }
    public int SkusSkipped { get; set; }
    public int ProductBarcodes { get; set; }
    public int VariantBarcodes { get; set; }
    public int Unplaced { get; set; }
    public List<string> Errors { get; set; } = new();
    public string Summary =>
        $"{RowsRead} rows · {ProductsMatched} products matched · {ProductBarcodes} product barcodes · " +
        $"{VariantBarcodes} variant barcodes · {SkusSkipped} SKUs skipped (not in catalogue) · {Unplaced} barcodes unplaced";
}

/// <summary>
/// Imports EposNow barcodes from a CSV (sku,color,barcode). Matches each SKU to our
/// Product.Sku and assigns the barcode(s): the product's primary barcode + per-variant
/// barcodes (colour-matched first, then filling remaining variant slots). Because our scan
/// resolves any barcode to its parent product, every assigned barcode becomes scannable.
/// </summary>
public class BarcodeImportService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<BarcodeImportService> _log;
    public BarcodeImportService(ApplicationDbContext db, ILogger<BarcodeImportService> log)
    {
        _db = db;
        _log = log;
    }

    private static string Norm(string? s) => new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    public async Task<BarcodeImportResult> ImportAsync(string csvPath, IProgress<string>? progress = null)
    {
        var r = new BarcodeImportResult();
        void Log(string m) { progress?.Report(m); _log.LogInformation("[barcode-import] {Msg}", m); }

        if (!File.Exists(csvPath)) { r.Errors.Add($"File not found: {csvPath}"); return r; }

        var rows = new List<(string Sku, string Color, string Barcode)>();
        foreach (var line in (await File.ReadAllLinesAsync(csvPath)).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var p = line.Split(',');
            if (p.Length < 3) continue;
            var sku = p[0].Trim(); var color = p[1].Trim(); var bc = p[2].Trim();
            if (sku.Length == 0 || bc.Length == 0) continue;
            rows.Add((sku, color, bc));
        }
        r.RowsRead = rows.Count;
        Log($"Read {rows.Count} barcode rows.");

        foreach (var grp in rows.GroupBy(x => x.Sku))
        {
            try
            {
                var product = await _db.Products
                    .Include(p => p.Variants).ThenInclude(v => v.AttributeValues).ThenInclude(av => av.Attribute)
                    .FirstOrDefaultAsync(p => p.Sku == grp.Key);
                if (product == null) { r.SkusSkipped++; continue; }
                r.ProductsMatched++;

                var list = grp.ToList();

                // Product-level barcode = first.
                product.Barcode = list[0].Barcode;
                product.UpdatedAt = DateTime.UtcNow;
                r.ProductBarcodes++;

                // Assign each row to a variant: colour-match first, then fill remaining slots.
                var used = new HashSet<int>();
                string VarColor(ProductVariant v) =>
                    Norm(v.AttributeValues.FirstOrDefault(av => av.Attribute != null && av.Attribute.Slug == "color")?.Value ?? v.Name);

                foreach (var row in list)
                {
                    var color = Norm(row.Color);
                    ProductVariant? v = null;
                    if (color.Length > 0)
                        v = product.Variants.FirstOrDefault(x => !used.Contains(x.Id) && VarColor(x) == color);
                    v ??= product.Variants.FirstOrDefault(x => !used.Contains(x.Id));
                    if (v != null) { v.Barcode = row.Barcode; used.Add(v.Id); r.VariantBarcodes++; }
                    else if (product.Variants.Count > 0) r.Unplaced++; // more rows than variant slots
                }

                await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                var inner = ex; while (inner.InnerException != null) inner = inner.InnerException;
                r.Errors.Add($"SKU {grp.Key}: {inner.Message}");
                _log.LogWarning(ex, "Barcode import error for SKU {Sku}", grp.Key);
            }
        }

        Log($"Done: {r.Summary}");
        return r;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Named-format import (POS "ProductList" export): "Name;CategoryId;Barcode" where the
    // SKU is the leading token of Name and the variant (colour and/or size) is embedded in
    // Name — e.g. "6012298 (BOW AND PEARL RING - SILVER)  SILVER 11;RINGS;011674".
    //
    // Matching is by SKU + variant STRUCTURE (colour and/or size), regardless of how the
    // product is named in our catalogue. A file row matches a variant when EVERY dimension
    // the variant actually uses agrees: (variant has no colour OR colours match) AND
    // (variant has no size OR sizes match). Supports a dry-run (commit=false) so the whole
    // result can be previewed before any barcode is written. Overwrites existing barcodes.
    // ─────────────────────────────────────────────────────────────────────────────

    private static readonly Regex SkuLead = new(@"^\s*([0-9A-Za-z]+)", RegexOptions.Compiled);
    // "SZ7", "SZE9", "SIZE 10" → the size. A bare size number is only trusted when it stands alone
    // (so "3" inside "3TONE" or "2" in "2PIECE" is not mistaken for a size).
    private static readonly Regex SzTag = new(@"(?:\bsz[e]?|\bsize)\s*0*([0-9]{1,2})(-5)?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NumTok = new(@"(?<![0-9a-zA-Z])0*([0-9]{1,2})(-5)?(?![0-9a-zA-Z])", RegexOptions.Compiled);

    // File colour shorthands → the catalogue's colour words (matching is token-based, so an entry
    // per canonical colour is enough; compound colours like "gold-multi-red" match token-by-token).
    private static readonly Dictionary<string, string> ColorSyn = new()
    {
        ["slvr"] = "silver", ["slv"] = "silver", ["silv"] = "silver", ["sliver"] = "silver",
        ["gld"] = "gold", ["gd"] = "gold",
        ["rsgld"] = "rosegold", ["rsgd"] = "rosegold", ["rosegld"] = "rosegold", ["rgold"] = "rosegold", ["rgld"] = "rosegold", ["rsgold"] = "rosegold",
        ["yelo"] = "yellow", ["yel0"] = "yellow", ["yel"] = "yellow", ["ylw"] = "yellow", ["yellw"] = "yellow",
        ["gren"] = "green", ["grn"] = "green",
        ["blu"] = "blue", ["blk"] = "black", ["pnk"] = "pink", ["wht"] = "white", ["crl"] = "coral",
        ["tone"] = "tone", ["3tone"] = "tone", ["2tone"] = "tone", ["tritone"] = "tone",
    };

    private static string Canon(string t) => ColorSyn.TryGetValue(t, out var c) ? c : t;

    /// <summary>Colour tokens of a string, synonym-normalised. Pure numbers (sizes) are dropped, a
    /// leading digit is stripped ("3tone"→"tone"), and "multiX" expands to {multi, X} so it lines up
    /// with the catalogue's hyphenated compounds.</summary>
    private static HashSet<string> ColorTokens(string s)
    {
        var set = new HashSet<string>();
        foreach (var raw in Regex.Split((s ?? "").ToLowerInvariant(), "[^a-z0-9]+"))
        {
            if (raw.Length == 0 || Regex.IsMatch(raw, @"^[0-9]+(-5)?$")) continue; // size number
            var t = Regex.Replace(raw, @"^s(?:z|ze|ize)0*[0-9]{1,2}", ""); // drop a glued size prefix e.g. sz9gold → gold
            if (t.Length == 0) continue;
            var dm = Regex.Match(t, @"^[0-9]+([a-z]+)$"); // digit+word e.g. 3tone → tone
            if (dm.Success) t = dm.Groups[1].Value;
            t = Canon(t);
            if (t.Length == 0) continue;
            if (t.StartsWith("multi") && t.Length > 5) { set.Add("multi"); set.Add(Canon(t.Substring(5))); }
            set.Add(t);
        }
        return set;
    }

    private static (HashSet<string> Colors, string Size) ParseVariant(ProductVariant v)
    {
        var name = v.Name ?? "";
        string size = "";
        foreach (var raw in name.Replace('/', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = Regex.Match(raw.Trim(), @"^0*([0-9]{1,2})(-5)?$");
            if (m.Success) { size = m.Groups[1].Value + (m.Groups[2].Success ? "-5" : ""); break; }
        }
        return (ColorTokens(name), size);
    }

    /// <summary>Pull the size the file row implies: an SZ/SIZE tag first, else the last standalone number.</summary>
    private static string ExtractSize(string nameNoSku)
    {
        var tag = SzTag.Match(nameNoSku);
        if (tag.Success) return tag.Groups[1].Value + (tag.Groups[2].Success ? "-5" : "");
        string last = "";
        foreach (Match m in NumTok.Matches(nameNoSku)) last = m.Groups[1].Value + (m.Groups[2].Success ? "-5" : "");
        return last;
    }

    public async Task<NamedBarcodeResult> ImportNamedAsync(IEnumerable<string> rawLines, bool commit, IProgress<string>? progress = null)
    {
        var result = new NamedBarcodeResult { Committed = commit };
        void Log(string m) { progress?.Report(m); _log.LogInformation("[barcode-import] {Msg}", m); }

        // Parse lines → (Sku, NameNoSku, Barcode). Tolerates a header row and the UTF-8 BOM.
        var parsed = new List<(string Sku, string Name, string NameNoSku, string Barcode)>();
        bool first = true;
        foreach (var line0 in rawLines)
        {
            var line = line0;
            if (first) { line = line.TrimStart('﻿'); }
            var isFirst = first; first = false;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parts = line.Split(';');
            if (parts.Length < 2) continue;
            var name = parts[0].Trim();
            var barcode = parts[^1].Trim();
            if (name.Length == 0 || barcode.Length == 0) continue;

            var skuM = SkuLead.Match(name);
            var sku = skuM.Success ? skuM.Groups[1].Value : "";
            // Header row (e.g. "Name;CategoryId;Barcode") — no leading numeric SKU, skip it.
            if (isFirst && (sku.Length == 0 || !char.IsDigit(sku[0]))) continue;
            if (sku.Length == 0) continue;

            var nameNoSku = name.Substring(skuM.Index + skuM.Length);
            parsed.Add((sku, name, nameNoSku, barcode));
        }
        result.RowsRead = parsed.Count;
        Log($"Parsed {parsed.Count} rows ({parsed.Select(p => p.Sku).Distinct().Count()} SKUs).");

        // Matching is pure — it only decides target → barcode. Writing happens once, globally,
        // in a two-phase pass so a barcode moving from a wrong variant to the right one doesn't
        // collide with the stale holder on the unique index.
        var variantAssign = new List<(int VariantId, string Barcode)>();
        var productAssign = new List<(int ProductId, string Barcode)>();

        foreach (var grp in parsed.GroupBy(x => x.Sku))
        {
            var product = await _db.Products
                .Include(p => p.Variants).ThenInclude(v => v.AttributeValues).ThenInclude(av => av.Attribute)
                .FirstOrDefaultAsync(p => p.Sku == grp.Key);

            if (product == null)
            {
                result.SkusNotFound++;
                result.NotFoundSkus.Add(grp.Key);
                foreach (var row in grp)
                    result.Rows.Add(new NamedBarcodeRow { Sku = grp.Key, Name = row.Name, Barcode = row.Barcode, Status = "sku-not-found" });
                continue;
            }
            result.ProductsMatched++;

            var variants = product.Variants.Select(v => (V: v, P: ParseVariant(v))).ToList();
            bool hasColor = variants.Any(x => x.P.Colors.Count > 0);
            bool hasSize = variants.Any(x => x.P.Size.Length > 0);
            var allDbColors = variants.SelectMany(x => x.P.Colors).ToHashSet();
            var used = new HashSet<int>();

            foreach (var row in grp)
            {
                var rr = new NamedBarcodeRow { Sku = grp.Key, Name = row.Name, Barcode = row.Barcode };
                var fSize = ExtractSize(row.NameNoSku);
                var fColors = ColorTokens(row.NameNoSku);
                rr.FileColor = allDbColors.Overlaps(fColors) ? string.Join("+", fColors.Intersect(allDbColors)) : null;
                rr.FileSize = fSize.Length > 0 ? fSize : null;

                if (variants.Count == 0)
                {
                    // Simple product — the barcode is the product's own.
                    rr.Status = "product-barcode";
                    rr.VariantName = "(no variants)";
                    productAssign.Add((product.Id, row.Barcode));
                    result.Assigned++;
                    result.Rows.Add(rr);
                    continue;
                }

                // Narrow by size first, then by colour — a variant's colour matches when its token
                // set is contained in the file row's tokens; the most-specific colour match wins.
                var pool = variants.Where(x => !used.Contains(x.V.Id) && (!hasSize || x.P.Size == fSize)).ToList();
                List<(ProductVariant V, (HashSet<string> Colors, string Size) P)> candidates;
                if (hasColor)
                {
                    var colMatch = pool.Where(x => x.P.Colors.Count > 0 && x.P.Colors.IsSubsetOf(fColors)).ToList();
                    var best = colMatch.Count > 0 ? colMatch.Max(x => x.P.Colors.Count) : 0;
                    candidates = colMatch.Where(x => x.P.Colors.Count == best).ToList();
                }
                else candidates = pool;

                if (candidates.Count == 1)
                {
                    var v = candidates[0].V;
                    rr.Status = "matched";
                    rr.VariantName = v.Name;
                    used.Add(v.Id);
                    variantAssign.Add((v.Id, row.Barcode));
                    result.Assigned++;
                }
                else if (candidates.Count == 0)
                {
                    rr.Status = "no-variant-match";
                    var fc = string.Join("+", fColors.Intersect(allDbColors));
                    rr.Detail = $"file[{(fc.Length > 0 ? fc : "-")}/{(fSize.Length > 0 ? fSize : "-")}] vs variants[{string.Join(", ", variants.Select(x => x.V.Name))}]";
                    result.NoVariantMatch++;
                }
                else
                {
                    rr.Status = "ambiguous";
                    rr.Detail = $"{candidates.Count} candidates: {string.Join(", ", candidates.Select(x => x.V.Name))}";
                    result.Ambiguous++;
                }
                result.Rows.Add(rr);
            }
        }

        if (commit && (variantAssign.Count > 0 || productAssign.Count > 0))
        {
            try { await WriteAssignmentsAsync(variantAssign, productAssign); }
            catch (Exception ex)
            {
                var inner = ex; while (inner.InnerException != null) inner = inner.InnerException;
                result.Errors.Add(inner.Message);
                _log.LogError(ex, "Named barcode import write failed");
            }
        }

        Log($"Done ({(commit ? "COMMIT" : "dry-run")}): {result.Summary}");
        return result;
    }

    /// <summary>
    /// Two-phase, single-transaction write. Barcodes are globally unique, and this import moves
    /// codes between variants (a code currently on the wrong variant must land on the right one).
    /// Phase 1 nulls every target barcode AND any current holder of an incoming code, so phase 2's
    /// assignments never collide with a stale value mid-transaction.
    /// </summary>
    private async Task WriteAssignmentsAsync(List<(int VariantId, string Barcode)> variantAssign,
        List<(int ProductId, string Barcode)> productAssign)
    {
        var incoming = variantAssign.Select(a => a.Barcode).Concat(productAssign.Select(a => a.Barcode)).ToHashSet();
        var targetVarIds = variantAssign.Select(a => a.VariantId).ToHashSet();
        var targetProdIds = productAssign.Select(a => a.ProductId).ToHashSet();

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync();

            // Phase 1: clear targets + any existing holder of an incoming code (variants and products).
            var varsToClear = await _db.ProductVariants
                .Where(v => targetVarIds.Contains(v.Id) || (v.Barcode != null && incoming.Contains(v.Barcode)))
                .ToListAsync();
            foreach (var v in varsToClear) v.Barcode = null;

            var prodsToClear = await _db.Products
                .Where(p => targetProdIds.Contains(p.Id) || (p.Barcode != null && incoming.Contains(p.Barcode)))
                .ToListAsync();
            foreach (var p in prodsToClear) p.Barcode = null;

            await _db.SaveChangesAsync();

            // Phase 2: assign. Last write wins if the file ever repeats a target.
            var varById = varsToClear.ToDictionary(v => v.Id);
            foreach (var (vid, bc) in variantAssign)
                if (varById.TryGetValue(vid, out var v)) { v.Barcode = bc; }

            var prodById = prodsToClear.Where(p => targetProdIds.Contains(p.Id)).ToDictionary(p => p.Id);
            var now = DateTime.UtcNow;
            foreach (var (pid, bc) in productAssign)
                if (prodById.TryGetValue(pid, out var p)) { p.Barcode = bc; p.UpdatedAt = now; }

            // Touch UpdatedAt on parents of assigned variants.
            var parentIds = await _db.ProductVariants.Where(v => targetVarIds.Contains(v.Id))
                .Select(v => v.ProductId).Distinct().ToListAsync();
            var parents = await _db.Products.Where(p => parentIds.Contains(p.Id)).ToListAsync();
            foreach (var p in parents) p.UpdatedAt = now;

            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        });
    }
}

public class NamedBarcodeRow
{
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public string Barcode { get; set; } = "";
    public string? FileColor { get; set; }
    public string? FileSize { get; set; }
    public string Status { get; set; } = "";   // matched | product-barcode | no-variant-match | ambiguous | sku-not-found
    public string? VariantName { get; set; }
    public string? Detail { get; set; }
}

public class NamedBarcodeResult
{
    public bool Committed { get; set; }
    public int RowsRead { get; set; }
    public int ProductsMatched { get; set; }
    public int Assigned { get; set; }
    public int SkusNotFound { get; set; }
    public int NoVariantMatch { get; set; }
    public int Ambiguous { get; set; }
    public List<NamedBarcodeRow> Rows { get; set; } = new();
    public List<string> NotFoundSkus { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public string Summary =>
        $"{RowsRead} rows · {Assigned} barcodes {(Committed ? "written" : "to write")} · {ProductsMatched} products matched · " +
        $"{SkusNotFound} SKUs discarded (not in catalogue) · {NoVariantMatch} rows no variant match · {Ambiguous} ambiguous";
}
