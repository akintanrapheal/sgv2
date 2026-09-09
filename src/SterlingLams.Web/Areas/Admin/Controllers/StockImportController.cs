using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic.FileIO;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    /// <summary>
    /// Bulk stock importer for moving EposNow (old-POS) on-hand counts into the new system. Upload a CSV
    /// with a Sku + Barcode column and one column per store; each matched product/variant has its on-hand
    /// at each store OVERWRITTEN to the value in the file (applied through the stock ledger as an
    /// Adjustment, so every change is traceable). Owner/full-admin only. Dry-run Preview, then Apply.
    /// A "Download template" export produces the same layout pre-filled with current quantities.
    /// </summary>
    public class StockImportController : AdminBaseController
    {
        protected override string Section => "Inventory";

        private readonly ApplicationDbContext _db;
        private readonly IStockService _stock;
        private readonly ILogger<StockImportController> _log;

        public StockImportController(ApplicationDbContext db, IStockService stock, ILogger<StockImportController> log)
        {
            _db = db;
            _stock = stock;
            _log = log;
        }

        private bool Denied => !AdminSections.IsFullAccess(User);

        public IActionResult Index()
        {
            if (Denied) return Forbid();
            ViewData["Title"] = "Import stock";
            return View();
        }

        // ── Download a template pre-filled with every product/variant + current per-store on-hand ──
        public async Task<IActionResult> Template()
        {
            if (Denied) return Forbid();

            var stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Id).ToListAsync();
            var storeNames = stores.Select(s => s.Name.Replace("Sterlin Glams ", "")).ToList();

            var inv = await _db.StoreInventories
                .Select(si => new { si.ProductId, si.ProductVariantId, si.StoreId, si.QuantityOnHand })
                .ToListAsync();
            var invMap = inv.ToDictionary(x => (x.ProductId, x.ProductVariantId, x.StoreId), x => x.QuantityOnHand);

            var products = await _db.Products.Where(p => p.IsActive)
                .Select(p => new
                {
                    p.Id, p.Name, p.Sku, p.Barcode,
                    Variants = p.Variants.Where(v => v.IsActive)
                        .Select(v => new { v.Id, v.Name, v.Sku, v.Barcode }).ToList()
                })
                .OrderBy(p => p.Name)
                .ToListAsync();

            var sb = new StringBuilder();
            var header = new List<string> { "Name", "Sku", "Barcode" };
            header.AddRange(storeNames);
            Csv.AppendRow(sb, header.ToArray());

            foreach (var p in products)
            {
                if (p.Variants.Count == 0)
                {
                    var row = new List<string> { p.Name, p.Sku ?? "", p.Barcode ?? "" };
                    row.AddRange(stores.Select(s => (invMap.GetValueOrDefault((p.Id, (int?)null, s.Id))).ToString()));
                    Csv.AppendRow(sb, row.ToArray());
                }
                else
                {
                    foreach (var v in p.Variants)
                    {
                        var row = new List<string>
                        {
                            $"{p.Name} — {v.Name}", v.Sku ?? p.Sku ?? "", v.Barcode ?? p.Barcode ?? ""
                        };
                        row.AddRange(stores.Select(s => (invMap.GetValueOrDefault((p.Id, (int?)v.Id, s.Id))).ToString()));
                        Csv.AppendRow(sb, row.ToArray());
                    }
                }
            }

            return File(Csv.ToBytes(sb), "text/csv", $"stock_template_{DateTime.UtcNow:yyyyMMdd}.csv");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestFormLimits(MultipartBodyLengthLimit = 52428800)] // 50 MB
        public Task<IActionResult> Preview(IFormFile? file) => RunAsync(file, apply: false);

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestFormLimits(MultipartBodyLengthLimit = 52428800)]
        public Task<IActionResult> Apply(IFormFile? file) => RunAsync(file, apply: true);

        private async Task<IActionResult> RunAsync(IFormFile? file, bool apply)
        {
            if (Denied) return Json(new { ok = false, error = "Owner access required." });
            if (file == null || file.Length == 0)
                return Json(new { ok = false, error = "Please choose the stock CSV file." });

            List<Dictionary<string, string>> rows;
            List<string> headers;
            try
            {
                await using var s = file.OpenReadStream();
                (headers, rows) = ParseCsv(s);
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = "Couldn't read the CSV: " + ex.Message });
            }

            // Map each store column header → storeId (by name, ignoring the "Sterlin Glams " prefix).
            var stores = await _db.Stores.Where(s => s.IsActive).ToListAsync();
            var storeCols = new List<(string Header, int StoreId, string StoreName)>();
            var ignoredCols = new List<string>();
            foreach (var h in headers)
            {
                if (h.Equals("Name", StringComparison.OrdinalIgnoreCase)
                    || h.Equals("Sku", StringComparison.OrdinalIgnoreCase)
                    || h.Equals("Barcode", StringComparison.OrdinalIgnoreCase)) continue;
                var store = stores.FirstOrDefault(s =>
                    s.Name.Replace("Sterlin Glams ", "").Trim().Equals(h.Trim(), StringComparison.OrdinalIgnoreCase)
                    || s.Name.Trim().Equals(h.Trim(), StringComparison.OrdinalIgnoreCase));
                if (store != null) storeCols.Add((h, store.Id, store.Name.Replace("Sterlin Glams ", "")));
                else ignoredCols.Add(h);
            }

            if (storeCols.Count == 0)
                return Json(new { ok = false, error = "No store columns recognised. Use one column per store, named e.g. " + string.Join(", ", stores.Select(s => s.Name.Replace("Sterlin Glams ", ""))) });

            // Barcode/SKU → (productId, variantId). Variant matches win over product-level.
            var variants = await _db.ProductVariants.Where(v => v.IsActive)
                .Select(v => new { v.ProductId, v.Id, v.Sku, v.Barcode }).ToListAsync();
            var prods = await _db.Products.Where(p => p.IsActive)
                .Select(p => new { p.Id, p.Sku, p.Barcode }).ToListAsync();
            var byBarcode = new Dictionary<string, (int pid, int? vid)>(StringComparer.OrdinalIgnoreCase);
            var bySku = new Dictionary<string, (int pid, int? vid)>(StringComparer.OrdinalIgnoreCase);
            void AddKey(Dictionary<string, (int, int?)> map, string? key, int pid, int? vid)
            { if (!string.IsNullOrWhiteSpace(key) && !map.ContainsKey(key.Trim())) map[key.Trim()] = (pid, vid); }
            // Variants FIRST: a variant product keeps its stock on the variant rows, and a product often
            // shares its SKU/barcode with a variant — so the specific variant must win over the pool.
            foreach (var v in variants) { AddKey(byBarcode, v.Barcode, v.ProductId, v.Id); AddKey(bySku, v.Sku, v.ProductId, v.Id); }
            foreach (var p in prods) { AddKey(byBarcode, p.Barcode, p.Id, null); AddKey(bySku, p.Sku, p.Id, null); }

            (int pid, int? vid)? Resolve(string sku, string barcode)
            {
                if (!string.IsNullOrWhiteSpace(barcode) && byBarcode.TryGetValue(barcode.Trim(), out var b)) return b;
                if (!string.IsNullOrWhiteSpace(sku) && bySku.TryGetValue(sku.Trim(), out var s)) return s;
                return null;
            }

            int matched = 0, unmatched = 0, cellsWithQty = 0, applied = 0;
            var unmatchedSamples = new List<string>();
            var samples = new List<object>();
            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

            foreach (var r in rows)
            {
                string G(string k) => r.TryGetValue(k, out var v) ? v.Trim() : "";
                var sku = G("Sku");
                var barcode = G("Barcode");
                var hit = Resolve(sku, barcode);
                if (hit == null)
                {
                    unmatched++;
                    if (unmatchedSamples.Count < 15) unmatchedSamples.Add($"{G("Name")} (sku:{sku} bc:{barcode})");
                    continue;
                }
                matched++;

                var perStore = new List<object>();
                foreach (var (header, storeId, storeName) in storeCols)
                {
                    if (!r.TryGetValue(header, out var qtyStr) || string.IsNullOrWhiteSpace(qtyStr)) continue;
                    if (!int.TryParse(qtyStr.Trim(), out var qty) || qty < 0) continue;
                    cellsWithQty++;
                    perStore.Add(new { store = storeName, qty });

                    if (apply)
                    {
                        var (pid, vid) = hit.Value;
                        var current = await _stock.GetStockAsync(pid, vid, storeId, fallback: false);
                        var delta = qty - current;
                        if (delta != 0)
                        {
                            await _stock.ApplyAsync(pid, vid, storeId, delta, StockMovementType.Adjustment,
                                "EposNow stock import", userId: userId, materializeVariant: vid.HasValue);
                            applied++;
                        }
                    }
                }
                if (samples.Count < 12 && perStore.Count > 0)
                    samples.Add(new { name = G("Name"), sku, barcode, stores = perStore });
            }

            if (apply)
            {
                await _db.SaveChangesAsync();
                await LogAsync("Import", "Inventory", null,
                    $"EposNow stock import: {matched} product(s) matched, {applied} store-quantity change(s) applied, {unmatched} row(s) unmatched.");
            }

            return Json(new
            {
                ok = true,
                applied = apply,
                totalRows = rows.Count,
                matched,
                unmatched,
                unmatchedSamples,
                cellsWithQty,
                changesApplied = applied,
                storeColumns = storeCols.Select(c => c.StoreName),
                ignoredColumns = ignoredCols,
                samples,
            });
        }

        // Robust CSV parse → (headers, rows). Same approach as the customer/product importers.
        private static (List<string> Headers, List<Dictionary<string, string>> Rows) ParseCsv(Stream stream)
        {
            var rows = new List<Dictionary<string, string>>();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var parser = new TextFieldParser(reader) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true };
            parser.SetDelimiters(",");
            if (parser.EndOfData) return (new(), rows);

            var headers = (parser.ReadFields() ?? Array.Empty<string>())
                .Select(h => h.Trim().TrimStart('﻿')).ToList();

            while (!parser.EndOfData)
            {
                var fields = parser.ReadFields() ?? Array.Empty<string>();
                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < headers.Count && i < fields.Length; i++)
                    row[headers[i]] = fields[i];
                rows.Add(row);
            }
            return (headers, rows);
        }
    }
}
