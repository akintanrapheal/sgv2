using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

/// <summary>
/// Imports per-store stock levels (and, optionally, the till/POS selling price) from an EposNow-style
/// export. Each row is <c>Name = "SKU (DESCRIPTION)"</c>, a unique <c>Barcode</c>, a <c>DatedStock</c>
/// on-hand quantity and a <c>SalePrice</c> (the EposNow till price). Rows are matched to a product or
/// variant by <b>barcode</b> (the reliable key — variants share a SKU but each has its own barcode).
///
/// Apply sets the selected store's on-hand quantity to DatedStock (an absolute "opening stock" set,
/// recorded on the ledger) and, when asked, writes SalePrice to <see cref="Product.PosPrice"/> /
/// <see cref="ProductVariant.PosPrice"/> — the POS-only price. It never touches the website
/// <c>Price</c> / <c>SalePrice</c>, so storefront pricing is unaffected.
/// </summary>
public class StockImportService
{
    private readonly ApplicationDbContext _db;
    public StockImportService(ApplicationDbContext db) => _db = db;

    public sealed record Row(string Name, string Sku, string Barcode, int Qty, decimal SalePrice);

    /// <summary>A row that could not be imported, with the reason, so the owner can fix it in the sheet.</summary>
    public sealed record Issue(string Sku, string Name, string Barcode, int Qty, decimal SalePrice, string Reason);

    public sealed record Result(
        bool Committed, int RowsRead, int Matched, int Unmatched,
        int StockLines, int TotalUnits, int PosPricesSet,
        List<Issue> Issues, string Summary, string? Error);

    // ── Parse the uploaded file into rows (xlsx via ClosedXML, or a simple CSV) ──────────────────
    public static List<Row> Parse(Stream stream, string fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        return ext == ".csv" ? ParseCsv(stream) : ParseXlsx(stream);
    }

    private static (string sku, string name) SplitName(string? raw)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0) return ("", "");
        // "SKU (DESCRIPTION)" → (SKU, DESCRIPTION). Fallback: leading token is the SKU.
        var m = Regex.Match(raw, @"^\s*(\S+)\s*\((.*)\)\s*$");
        if (m.Success) return (m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim());
        var sp = raw.IndexOf(' ');
        return sp > 0 ? (raw[..sp].Trim(), raw[(sp + 1)..].Trim()) : (raw, "");
    }

    /// <summary>Barcode key for leading-zero-tolerant matching: trims whitespace then leading zeros,
    /// so "010118" and "10118" compare equal. All-zero / empty collapses to "".</summary>
    private static string NormalizeBarcode(string? b)
    {
        b = (b ?? "").Trim();
        if (b.Length == 0) return "";
        var t = b.TrimStart('0');
        return t;   // "" when the code was all zeros — treated as no usable key
    }

    private static int ToQty(string? s)
        => int.TryParse((s ?? "").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var q) ? q
         : (double.TryParse((s ?? "").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? (int)Math.Round(d) : 0);

    private static decimal ToMoney(string? s)
        => decimal.TryParse((s ?? "").Trim().Replace("₦", "").Replace(",", ""),
            NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;

    private static List<Row> ParseXlsx(Stream stream)
    {
        using var wb = new XLWorkbook(stream);
        var ws = wb.Worksheets.First();
        var header = ws.FirstRowUsed() ?? throw new InvalidOperationException("The sheet is empty.");
        // Map header name → column number (case-insensitive), tolerant of extra columns/order.
        var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in header.CellsUsed())
            col[c.GetString().Trim()] = c.Address.ColumnNumber;

        int Col(params string[] names)
        {
            foreach (var n in names) if (col.TryGetValue(n, out var i)) return i;
            return 0;
        }
        var cName = Col("Name", "Product Name", "Product", "Title");
        var cSku = Col("SKU", "Sku");
        var cBarcode = Col("Barcode", "EAN", "Code");
        var cQty = Col("DatedStock", "Current Stock", "Stock", "Quantity", "Qty", "On Hand", "OnHand");
        var cPrice = Col("SalePrice", "Sale Price", "Price", "RetailPrice", "Retail Price");
        if (cName == 0 && cBarcode == 0 && cSku == 0)
            throw new InvalidOperationException("Couldn't find a 'Name', 'SKU' or 'Barcode' column in the sheet.");

        string Cell(IXLRow row, int c)
        {
            if (c == 0) return "";
            var cell = row.Cell(c);
            if (cell.DataType == XLDataType.Number)
            {
                var d = cell.GetDouble();
                return d == Math.Floor(d) ? ((long)d).ToString(CultureInfo.InvariantCulture)
                                          : d.ToString(CultureInfo.InvariantCulture);
            }
            return cell.GetString().Trim();
        }

        var rows = new List<Row>();
        foreach (var r in ws.RowsUsed().Skip(1)) // skip header
        {
            var rawName = Cell(r, cName);
            var barcode = Cell(r, cBarcode);
            var skuCell = Cell(r, cSku);
            if (rawName.Length == 0 && barcode.Length == 0 && skuCell.Length == 0) continue;
            // SKU from its own column when present (e.g. the per-category export); otherwise parse it
            // out of a "SKU (DESCRIPTION)" Name. Display name = the description, or the raw name.
            var (parsedSku, desc) = SplitName(rawName);
            var sku = skuCell.Length > 0 ? skuCell : parsedSku;
            var display = desc.Length > 0 ? desc : rawName;
            rows.Add(new Row(display, sku, barcode, ToQty(Cell(r, cQty)), ToMoney(Cell(r, cPrice))));
        }
        return rows;
    }

    private static List<Row> ParseCsv(Stream stream)
    {
        using var sr = new StreamReader(stream);
        var text = sr.ReadToEnd().Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = text.Split('\n').Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count < 2) return new();
        var delim = lines[0].Contains(';') && !lines[0].Contains(',') ? ';' : ',';
        string[] Split(string l) => SplitCsvLine(l, delim);

        var headers = Split(lines[0]);
        int Idx(params string[] names)
        {
            foreach (var n in names)
            {
                var i = Array.FindIndex(headers, h => h.Trim().Equals(n, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) return i;
            }
            return -1;
        }
        int iName = Idx("Name", "Product Name", "Product", "Title"), iSku = Idx("SKU", "Sku"),
            iBc = Idx("Barcode", "EAN", "Code"),
            iQty = Idx("DatedStock", "Current Stock", "Stock", "Quantity", "Qty", "On Hand", "OnHand"),
            iPrice = Idx("SalePrice", "Sale Price", "Price", "RetailPrice", "Retail Price");

        var rows = new List<Row>();
        for (int li = 1; li < lines.Count; li++)
        {
            var f = Split(lines[li]);
            string At(int i) => i >= 0 && i < f.Length ? f[i].Trim() : "";
            var rawName = At(iName); var barcode = At(iBc); var skuCell = At(iSku);
            if (rawName.Length == 0 && barcode.Length == 0 && skuCell.Length == 0) continue;
            var (parsedSku, desc) = SplitName(rawName);
            var sku = skuCell.Length > 0 ? skuCell : parsedSku;
            var display = desc.Length > 0 ? desc : rawName;
            rows.Add(new Row(display, sku, barcode, ToQty(At(iQty)), ToMoney(At(iPrice))));
        }
        return rows;
    }

    private static string[] SplitCsvLine(string line, char delim)
    {
        var outp = new List<string>(); var sb = new System.Text.StringBuilder(); bool q = false;
        foreach (var ch in line)
        {
            if (ch == '"') q = !q;
            else if (ch == delim && !q) { outp.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        outp.Add(sb.ToString());
        return outp.Select(s => s.Trim().Trim('"')).ToArray();
    }

    // ── Preview / apply ─────────────────────────────────────────────────────────────────────────
    public async Task<Result> RunAsync(Stream file, string fileName, int storeId,
        bool setStock, bool setPosPrice, bool commit, string? userId)
    {
        List<Row> rows;
        try { rows = Parse(file, fileName); }
        catch (Exception ex) { return new Result(false, 0, 0, 0, 0, 0, 0, new(), "", "Couldn't read the file: " + ex.Message); }
        if (rows.Count == 0) return new Result(false, 0, 0, 0, 0, 0, 0, new(), "", "No data rows found in the file.");

        var store = await _db.Stores.FirstOrDefaultAsync(s => s.Id == storeId && s.IsActive);
        if (store == null) return new Result(false, rows.Count, 0, 0, 0, 0, 0, new(), "", "Pick an active store.");

        // barcode → (productId, variantId?). Variant barcodes win over product barcodes.
        var map = new Dictionary<string, (int pid, int? vid)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in await _db.Products.Where(p => p.Barcode != null && p.Barcode != "")
                     .Select(p => new { p.Id, p.Barcode }).ToListAsync())
            map[p.Barcode!.Trim()] = (p.Id, null);
        foreach (var v in await _db.ProductVariants.Where(v => v.Barcode != null && v.Barcode != "")
                     .Select(v => new { v.Id, v.ProductId, v.Barcode }).ToListAsync())
            map[v.Barcode!.Trim()] = (v.ProductId, v.Id);

        // Leading-zero tolerance: the catalogue sometimes stores a barcode with a leading zero
        // (e.g. "010118" from an earlier load) while the stock sheet has it bare ("10118"). Build a
        // second index keyed by the zero-stripped barcode so the two match. A normalised key that two
        // DIFFERENT products share is marked ambiguous and never used, so we can't mis-assign.
        var normMap = new Dictionary<string, (int pid, int? vid)>();
        var normAmbig = new HashSet<string>();
        foreach (var kv in map)
        {
            var n = NormalizeBarcode(kv.Key);
            if (n.Length == 0) continue;
            if (normMap.TryGetValue(n, out var ex)) { if (!ex.Equals(kv.Value)) normAmbig.Add(n); }
            else normMap[n] = kv.Value;
        }

        // Barcodes that appear on more than one row in the file — ambiguous, so we skip them (which
        // product would the quantity belong to?) and list them for the owner to fix.
        var dupBc = rows.Where(r => !string.IsNullOrWhiteSpace(r.Barcode))
            .GroupBy(r => r.Barcode.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var matchedRows = new List<(Row row, int pid, int? vid)>();
        var issues = new List<Issue>();
        int matched = 0;
        foreach (var r in rows)
        {
            var bc = (r.Barcode ?? "").Trim();
            if (bc.Length == 0)
            { issues.Add(new Issue(r.Sku, r.Name, bc, r.Qty, r.SalePrice, "No barcode in this row")); continue; }
            if (dupBc.TryGetValue(bc, out var n))
            { issues.Add(new Issue(r.Sku, r.Name, bc, r.Qty, r.SalePrice, $"Duplicate barcode in file ({n}×) — skipped")); continue; }
            // Exact barcode first; then the leading-zero-tolerant index (e.g. file "10118" ↔ stored "010118").
            if (!map.TryGetValue(bc, out var t))
            {
                var nb = NormalizeBarcode(bc);
                if (nb.Length == 0 || normAmbig.Contains(nb) || !normMap.TryGetValue(nb, out t))
                { issues.Add(new Issue(r.Sku, r.Name, bc, r.Qty, r.SalePrice, "Barcode not found in catalogue")); continue; }
            }
            matched++; matchedRows.Add((r, t.pid, t.vid));
        }
        int unmatched = issues.Count;

        // Current on-hand for this store, keyed by (product, variant).
        var invByKey = (await _db.StoreInventories.Where(si => si.StoreId == storeId).ToListAsync())
            .ToDictionary(si => (si.ProductId, si.ProductVariantId));

        // Entities to (maybe) price — preloaded in bulk so apply touches no extra queries per row.
        Dictionary<int, Product> products = new();
        Dictionary<int, ProductVariant> variants = new();
        if (setPosPrice)
        {
            var pids = matchedRows.Select(m => m.pid).Distinct().ToList();
            var vids = matchedRows.Where(m => m.vid != null).Select(m => m.vid!.Value).Distinct().ToList();
            products = await _db.Products.Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
            variants = await _db.ProductVariants.Where(v => vids.Contains(v.Id)).ToDictionaryAsync(v => v.Id);
        }

        var now = DateTime.UtcNow;
        int stockLines = 0, totalUnits = 0, posSet = 0;
        foreach (var (row, pid, vid) in matchedRows)
        {
            if (setStock)
            {
                totalUnits += Math.Max(0, row.Qty);
                invByKey.TryGetValue((pid, vid), out var si);
                var current = si?.QuantityOnHand ?? 0;
                var target = Math.Max(0, row.Qty);
                if (target != current)
                {
                    stockLines++;
                    if (commit)
                    {
                        if (si == null)
                        {
                            si = new StoreInventory { ProductId = pid, ProductVariantId = vid, StoreId = storeId, QuantityOnHand = 0, UpdatedAt = now };
                            _db.StoreInventories.Add(si); invByKey[(pid, vid)] = si;
                        }
                        _db.StockMovements.Add(new StockMovement
                        {
                            ProductId = pid, ProductVariantId = vid, StoreId = storeId,
                            QuantityChange = target - current, BalanceAfter = target,
                            Type = StockMovementType.Adjustment,
                            Reference = "StockImport", Note = $"Stock import — {store.Name}",
                            CreatedByUserId = userId, CreatedAt = now,
                        });
                        si.QuantityOnHand = target; si.UpdatedAt = now;
                    }
                }
            }

            if (setPosPrice && row.SalePrice > 0)
            {
                if (vid != null && variants.TryGetValue(vid.Value, out var ve))
                { if (ve.PosPrice != row.SalePrice) { posSet++; if (commit) ve.PosPrice = row.SalePrice; } }
                else if (products.TryGetValue(pid, out var pe))
                { if (pe.PosPrice != row.SalePrice) { posSet++; if (commit) pe.PosPrice = row.SalePrice; } }
            }
        }

        if (commit) await _db.SaveChangesAsync();

        var summary = $"{store.Name}: {matched} matched / {unmatched} unmatched; "
                    + $"{(commit ? "set" : "would set")} {stockLines} stock line(s) ({totalUnits} units)"
                    + (setPosPrice ? $", {posSet} EPOS price(s)" : "") + ".";
        return new Result(commit, rows.Count, matched, unmatched, stockLines, totalUnits, posSet, issues, summary, null);
    }
}
