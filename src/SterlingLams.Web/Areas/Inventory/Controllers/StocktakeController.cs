using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

public class StocktakeController : InventoryAreaController
{
    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;
    private readonly IStoreAccessService _access;
    private const int PageSize = 25;

    // Reasons offered for a counted difference (EPOS-style).
    public static readonly string[] LineReasons =
        { "External Branch Movement", "Internal Movement", "Missing Stock", "New Stock", "Stock Take" };

    public StocktakeController(ApplicationDbContext db, IStockService stock, IStoreAccessService access)
    {
        _db = db;
        _stock = stock;
        _access = access;
    }

    // Back Office Stock Take: pick staff + location, then count (search/scan → list → review → complete).
    // A count auto-saves as a Draft, so a refresh / leaving the page can be resumed (draftId).
    public async Task<IActionResult> Index(int? storeId, string? staffId, int? draftId)
    {
        ViewData["Title"] = "Stock Take";
        ViewBag.Stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        ViewBag.Staff = await StaffOptionsAsync();
        ViewBag.Reasons = LineReasons;

        // Resume a saved draft → preload its branch, staff and working list.
        if (draftId.HasValue)
        {
            var draft = await _db.StockTakes.FirstOrDefaultAsync(t => t.Id == draftId.Value && t.Status == "Draft");
            if (draft != null && await _access.CanWriteAsync(User, draft.StoreId))
            {
                storeId = draft.StoreId;
                staffId = draft.StaffUserId;
                ViewBag.DraftId = draft.Id;
                ViewBag.DraftJson = draft.DraftJson ?? "[]";
                ViewBag.DraftNote = draft.Note;
            }
        }

        ViewBag.StoreId = storeId;
        ViewBag.StaffId = staffId;
        var store = storeId.HasValue ? await _db.Stores.FirstOrDefaultAsync(s => s.Id == storeId.Value) : null;
        ViewBag.StoreName = store?.Name ?? "";

        // Open drafts for the "Resume previous" panel on the start screen.
        if (storeId == null) ViewBag.Drafts = await OpenDraftsAsync();
        return View();
    }

    // Open (unfinished) drafts the current user may work on, newest first.
    private async Task<List<object>> OpenDraftsAsync()
    {
        var drafts = await _db.StockTakes.Include(t => t.Store)
            .Where(t => t.Status == "Draft")
            .OrderByDescending(t => t.UpdatedAt ?? t.CreatedAt).ToListAsync();
        var result = new List<object>();
        foreach (var d in drafts)
        {
            if (!await _access.CanWriteAsync(User, d.StoreId)) continue;
            int count = 0;
            try { count = System.Text.Json.JsonSerializer.Deserialize<List<DraftLine>>(d.DraftJson ?? "[]")?.Count ?? 0; }
            catch { /* malformed draft → show 0 */ }
            result.Add(new { id = d.Id, storeName = d.Store.Name, staffName = d.StaffName,
                updatedAt = d.UpdatedAt ?? d.CreatedAt, itemCount = count });
        }
        return result;
    }

    // Typeahead for the count box — name / SKU / barcode, with the system (expected) qty for the branch.
    [HttpGet]
    public async Task<IActionResult> StSearch(string q, int storeId)
    {
        q = (q ?? "").Trim();
        if (q.Length < 2) return Json(Array.Empty<object>());
        // Match on the product OR any of its variants (name/sku/barcode). A variant product returns ONE
        // row per option so the counter adds — and counts — the exact variant, never a shared pool.
        var products = await _db.Products.Include(p => p.Variants).Include(p => p.Category)
            .Where(p => p.IsActive && (
                EF.Functions.ILike(p.Name, $"%{q}%")
                || EF.Functions.ILike(p.Sku ?? "", $"%{q}%")
                || EF.Functions.ILike(p.Barcode ?? "", $"%{q}%")
                || p.Variants.Any(v => v.IsActive && (EF.Functions.ILike(v.Sku ?? "", $"%{q}%") || EF.Functions.ILike(v.Barcode ?? "", $"%{q}%")))))
            .OrderBy(p => p.Name).Take(20).ToListAsync();

        var pids = products.Select(p => p.Id).ToList();
        var inv = await _db.StoreInventories.Where(si => si.StoreId == storeId && pids.Contains(si.ProductId))
            .Select(si => new { si.ProductId, si.ProductVariantId, si.QuantityOnHand }).ToListAsync();
        int Qty(int pid, int? vid) => inv.FirstOrDefault(x => x.ProductId == pid && x.ProductVariantId == vid)?.QuantityOnHand ?? 0;

        var rows = new List<object>();
        foreach (var p in products)
        {
            var cat = p.Category?.Name ?? "";
            var vars = p.Variants.Where(v => v.IsActive).OrderBy(v => v.Name).ToList();
            if (vars.Count == 0)
                rows.Add(new { id = p.Id, variantId = (int?)null, name = p.Name, sku = p.Sku, barcode = p.Barcode, category = cat, expected = Qty(p.Id, null) });
            else
                foreach (var v in vars)
                    rows.Add(new { id = p.Id, variantId = (int?)v.Id, name = $"{p.Name} – {v.Name}", sku = v.Sku ?? p.Sku, barcode = v.Barcode ?? p.Barcode, category = cat, expected = Qty(p.Id, v.Id) });
            if (rows.Count >= 30) break;
        }
        return Json(rows);
    }

    // Exact barcode/SKU lookup for the scan box.
    [HttpGet]
    public async Task<IActionResult> ScanLookup(string code, int storeId)
    {
        code = (code ?? "").Trim();
        if (code.Length == 0) return Json(new { found = false });

        // 1) A variant's own barcode/SKU → count THAT exact variant (its own expected qty).
        var v = await _db.ProductVariants.Include(x => x.Product).ThenInclude(p => p.Category)
            .FirstOrDefaultAsync(x => x.IsActive && x.Product.IsActive && (x.Barcode == code || x.Sku == code));
        if (v != null)
        {
            var exp = await _stock.GetStockAsync(v.ProductId, v.Id, storeId, fallback: false);
            return Json(new { found = true, id = v.ProductId, variantId = (int?)v.Id,
                name = $"{v.Product.Name} – {v.Name}", sku = v.Sku ?? v.Product.Sku, barcode = v.Barcode,
                category = v.Product.Category != null ? v.Product.Category.Name : "", expected = exp });
        }

        // 2) A product's own barcode/SKU.
        var p = await _db.Products.Include(x => x.Variants).Include(x => x.Category)
            .FirstOrDefaultAsync(x => x.IsActive && (x.Barcode == code || x.Sku == code));
        if (p == null) return Json(new { found = false });

        var vars = p.Variants.Where(x => x.IsActive).OrderBy(x => x.Name).ToList();
        if (vars.Count == 0)
        {
            var exp = await _stock.GetStockAsync(p.Id, null, storeId, fallback: false);
            return Json(new { found = true, id = p.Id, variantId = (int?)null, name = p.Name, sku = p.Sku,
                barcode = p.Barcode, category = p.Category != null ? p.Category.Name : "", expected = exp });
        }

        // Variant product scanned by its PRODUCT code → let the counter pick which option.
        var cat = p.Category?.Name ?? "";
        var invp = await _db.StoreInventories.Where(si => si.StoreId == storeId && si.ProductId == p.Id)
            .Select(si => new { si.ProductVariantId, si.QuantityOnHand }).ToListAsync();
        int Qty(int? vid) => invp.FirstOrDefault(x => x.ProductVariantId == vid)?.QuantityOnHand ?? 0;
        var variants = vars.Select(x => new { id = p.Id, variantId = (int?)x.Id, name = $"{p.Name} – {x.Name}",
            sku = x.Sku ?? p.Sku, barcode = x.Barcode ?? p.Barcode, category = cat, expected = Qty(x.Id) }).ToList();
        return Json(new { found = false, needsVariant = true, name = p.Name, variants });
    }

    public class CountLine { public int ProductId { get; set; } public int? VariantId { get; set; } public int Counted { get; set; } public string? Reason { get; set; } }
    public class CompleteRequest { public int? DraftId { get; set; } public int StoreId { get; set; } public string? StaffId { get; set; } public string? Note { get; set; } public List<CountLine> Lines { get; set; } = new(); }

    // One row of the in-progress working list (mirrors the JS line: partial counts kept as text).
    public class DraftLine { public int Id { get; set; } public int? VariantId { get; set; } public string? Name { get; set; } public string? Barcode { get; set; } public string? Category { get; set; } public int Expected { get; set; } public string? Counted { get; set; } public string? Reason { get; set; } }
    public class SaveDraftRequest { public int? DraftId { get; set; } public int StoreId { get; set; } public string? StaffId { get; set; } public string? Note { get; set; } public List<DraftLine> Lines { get; set; } = new(); }

    // Auto-save the in-progress count as a Draft so a refresh / leaving the page can be resumed.
    // No stock is touched here — reconciliation happens only on Complete.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveDraft([FromBody] SaveDraftRequest req)
    {
        var store = await _db.Stores.FirstOrDefaultAsync(s => s.Id == req.StoreId && s.IsActive);
        if (store == null) return Json(new { success = false });
        if (!await _access.CanWriteAsync(User, store.Id)) return Json(new { success = false });

        var draft = req.DraftId.HasValue
            ? await _db.StockTakes.FirstOrDefaultAsync(t => t.Id == req.DraftId.Value && t.Status == "Draft")
            : null;
        if (draft != null && !await _access.CanWriteAsync(User, draft.StoreId)) return Json(new { success = false });

        var lines = req.Lines ?? new();
        // Empty working list → drop an existing draft (nothing worth resuming), otherwise no-op.
        if (lines.Count == 0)
        {
            if (draft != null) { _db.StockTakes.Remove(draft); await _db.SaveChangesAsync(); }
            return Json(new { success = true, draftId = (int?)null });
        }

        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var staffId = await IsStaffAsync(req.StaffId) ? req.StaffId : userId;
        var staffName = await StaffNameAsync(staffId) ?? "—";
        var json = System.Text.Json.JsonSerializer.Serialize(lines,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

        if (draft == null)
        {
            draft = new StockTake
            {
                Reference = "", StoreId = store.Id, StaffUserId = staffId, StaffName = staffName,
                Status = "Draft", Note = req.Note, DraftJson = json,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            };
            _db.StockTakes.Add(draft);
        }
        else
        {
            draft.StoreId = store.Id; draft.StaffUserId = staffId; draft.StaffName = staffName;
            draft.Note = req.Note; draft.DraftJson = json; draft.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return Json(new { success = true, draftId = draft.Id });
    }

    // Discard an unfinished draft from the "Resume previous" list.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DiscardDraft(int id)
    {
        var draft = await _db.StockTakes.FirstOrDefaultAsync(t => t.Id == id && t.Status == "Draft");
        if (draft != null && await _access.CanWriteAsync(User, draft.StoreId))
        {
            _db.StockTakes.Remove(draft);
            await _db.SaveChangesAsync();
            await LogAsync("Delete", "StockTake", id.ToString(), "Discarded stock-take draft");
        }
        return RedirectToAction(nameof(Index));
    }

    // Complete the stock-take: persist the StockTake record + reconcile each counted line through the
    // ledger (reason "Stock-take"). Returns the new stock-take id for redirect to its details.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Complete([FromBody] CompleteRequest req)
    {
        var store = await _db.Stores.FirstOrDefaultAsync(s => s.Id == req.StoreId && s.IsActive);
        if (store == null) return Json(new { success = false, message = "Invalid branch." });
        if (!await _access.CanWriteAsync(User, store.Id))
            return Json(new { success = false, message = "You can only run a stock-take for your assigned branch(es)." });
        if (req.Lines == null || req.Lines.Count == 0) return Json(new { success = false, message = "Nothing to count." });

        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        // Only accept a real staff member as the counter; otherwise fall back to the acting user.
        var staffId = await IsStaffAsync(req.StaffId) ? req.StaffId : userId;
        var staffName = await StaffNameAsync(staffId) ?? "—";

        var ids = req.Lines.Select(l => l.ProductId).Distinct().ToList();
        var products = await _db.Products.Include(p => p.Category)
            .Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        // Variant lines carry a VariantId — load those so we can reconcile each variant's own row and
        // snapshot its name/barcode. (Stock-take is now variant-aware; no more pool-only restriction.)
        var vids = req.Lines.Where(l => l.VariantId.HasValue).Select(l => l.VariantId!.Value).Distinct().ToList();
        var variants = await _db.ProductVariants.Where(v => vids.Contains(v.Id)).ToDictionaryAsync(v => v.Id);
        var valid = req.Lines.Where(l => l.Counted >= 0 && products.ContainsKey(l.ProductId)
            && (!l.VariantId.HasValue || variants.ContainsKey(l.VariantId.Value))).ToList();
        if (valid.Count == 0) return Json(new { success = false, message = "No valid items." });

        var seq = await NextRefAsync();
        // Completing a resumed draft reuses that row (Draft → Completed); otherwise a fresh record.
        StockTake take;
        if (req.DraftId.HasValue)
        {
            take = await _db.StockTakes.FirstOrDefaultAsync(t => t.Id == req.DraftId.Value && t.Status == "Draft");
            if (take == null) return Json(new { success = false, message = "Draft not found — it may already be completed." });
            if (!await _access.CanWriteAsync(User, take.StoreId)) return Json(new { success = false, message = "No access to that draft." });
            take.Reference = $"ST{seq:D5}"; take.StoreId = store.Id; take.StaffUserId = staffId;
            take.StaffName = staffName; take.Status = "Completed"; take.Note = req.Note;
            take.CreatedAt = DateTime.UtcNow; take.UpdatedAt = null; take.DraftJson = null;
        }
        else
        {
            take = new StockTake
            {
                Reference = $"ST{seq:D5}", StoreId = store.Id, StaffUserId = staffId,
                StaffName = staffName, Status = "Completed", Note = req.Note, CreatedAt = DateTime.UtcNow
            };
            _db.StockTakes.Add(take);
        }

        await using var tx = await _db.Database.BeginTransactionAsync();
        if (_db.Database.IsNpgsql())
            foreach (var pid in valid.Select(l => l.ProductId).Distinct().OrderBy(id => id))
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM \"StoreInventories\" WHERE \"ProductId\" = {pid} AND \"StoreId\" = {store.Id} FOR UPDATE");

        foreach (var l in valid)
        {
            var p = products[l.ProductId];
            var variant = l.VariantId.HasValue ? variants[l.VariantId.Value] : null;
            var current = await _stock.GetStockAsync(l.ProductId, l.VariantId, store.Id, fallback: false);
            var delta = l.Counted - current;
            if (delta != 0)
                await _stock.ApplyAsync(l.ProductId, l.VariantId, store.Id, delta, StockMovementType.Adjustment,
                    take.Reference, note: l.Reason ?? "Stock-take", userId: userId, materializeVariant: true);
            take.Lines.Add(new StockTakeLine
            {
                ProductId = p.Id,
                ProductVariantId = l.VariantId,
                ProductName = variant != null ? $"{p.Name} – {variant.Name}" : p.Name,
                Barcode = variant != null ? variant.Barcode : p.Barcode,
                CategoryName = p.Category?.Name ?? "", ExpectedQty = current, CountedQty = l.Counted,
                Reason = delta != 0 ? l.Reason : null
            });
        }

        try
        {
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Json(new { success = false, message = "Stock changed during the count — recount the affected items and complete again." });
        }

        await LogAsync("Create", "StockTake", take.Id.ToString(), $"Stock-take {take.Reference} at {store.Name} — {take.Lines.Count} item(s)");
        return Json(new { success = true, id = take.Id, reference = take.Reference });
    }

    private async Task<int> NextRefAsync()
    {
        // Ignore drafts (blank Reference) — take the max "ST#####" among real records.
        var refs = await _db.StockTakes
            .Where(t => t.Reference != null && t.Reference.StartsWith("ST"))
            .Select(t => t.Reference).ToListAsync();
        var max = refs.Where(r => int.TryParse(r[2..], out _)).Select(r => int.Parse(r[2..]))
            .DefaultIfEmpty(0).Max();
        return max + 1;
    }

    // Stock Takes history — date range + location filter + barcode/ref search.
    public async Task<IActionResult> History(DateTime? from, DateTime? to, int? storeId, string? q, int page = 1, string? format = null)
    {
        ViewData["Title"] = "Stock Takes";
        ViewBag.Stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();

        // Lagos days, like every other dated screen — see Services/ReportCalendar.
        var fromLocal = (from ?? ReportCalendar.Today.AddDays(-30)).Date;
        var toLocal = (to ?? ReportCalendar.Today).Date;
        var fromD = ReportCalendar.StartOfDayUtc(fromLocal);
        var toD = ReportCalendar.StartOfDayUtc(toLocal.AddDays(1));

        var query = _db.StockTakes.Include(t => t.Store).Include(t => t.Lines)
            .Where(t => t.Status == "Completed" && t.CreatedAt >= fromD && t.CreatedAt < toD);
        if (storeId.HasValue) query = query.Where(t => t.StoreId == storeId.Value);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(t => EF.Functions.ILike(t.Reference, $"%{q.Trim()}%"));

        if (format == "csv")
        {
            var all = await query.OrderByDescending(t => t.Id).ToListAsync();
            var sb = new System.Text.StringBuilder();
            // Csv handles quoting and neutralises anything a spreadsheet would execute; timestamps in
            // WAT like the rest of the admin.
            Csv.AppendRow(sb, "Stock Ref", "Location", "Staff", "Date (WAT)", "Items", "Discrepancies");
            foreach (var t in all)
                Csv.AppendRow(sb, t.Reference, t.Store.Name, t.StaffName,
                    ReportCalendar.ToLocal(t.CreatedAt).ToString("yyyy-MM-dd HH:mm"),
                    t.ItemCount.ToString(), t.Discrepancies.ToString());
            return File(Csv.ToBytes(sb), "text/csv", $"stock_takes_{DateTime.UtcNow:yyyyMMdd}.csv");
        }

        var total = await query.CountAsync();
        var takes = await query.OrderByDescending(t => t.Id)
            .Skip((page - 1) * PageSize).Take(PageSize).ToListAsync();

        // Which of these counts included a product that keeps its stock per option. Those lines were
        // applied to the product as a whole and never reached the option counts (see Details), so the
        // list marks them rather than making someone open each one to find out.
        var pageProductIds = takes.SelectMany(t => t.Lines).Select(l => l.ProductId).Distinct().ToList();
        var optionProductIds = (await _db.ProductVariants
                .Where(v => pageProductIds.Contains(v.ProductId) && v.IsActive)
                .Select(v => v.ProductId).Distinct().ToListAsync())
            .ToHashSet();
        ViewBag.AffectedTakeIds = takes
            .Where(t => t.Lines.Any(l => optionProductIds.Contains(l.ProductId)))
            .Select(t => t.Id).ToHashSet();

        ViewBag.From = fromLocal; ViewBag.To = toLocal;
        ViewBag.StoreId = storeId; ViewBag.Q = q;
        ViewBag.Page = page; ViewBag.TotalPages = (int)Math.Ceiling(total / (double)PageSize); ViewBag.Total = total;
        return View(takes);
    }

    // One completed stock-take's details (header + counted lines + variance).
    public async Task<IActionResult> Details(int id)
    {
        var take = await _db.StockTakes.Include(t => t.Store).Include(t => t.Lines)
            .FirstOrDefaultAsync(t => t.Id == id);
        if (take == null) return NotFound();
        ViewData["Title"] = $"Stock Take {take.Reference}";

        // Counts taken before 2026-07-31 could include products that keep their stock per option.
        // This screen counts a product as ONE number and applied it to the product-level row, so
        // those lines never touched the real per-option counts and left the units unsellable. Flag
        // them here so an old count can be put right — new counts refuse those products outright.
        var lineProductIds = take.Lines.Select(l => l.ProductId).Distinct().ToList();
        ViewBag.OptionProductIds = (await _db.ProductVariants
                .Where(v => lineProductIds.Contains(v.ProductId) && v.IsActive)
                .Select(v => v.ProductId).Distinct().ToListAsync())
            .ToHashSet();

        return View(take);
    }
}
