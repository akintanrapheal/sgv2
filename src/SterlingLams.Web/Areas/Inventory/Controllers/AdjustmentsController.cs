using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

// Moniebook-style stock-adjustment records: every grouped change (grid save or the dedicated
// form) is filed under a "BSA#####" header with a branch, reason and lines. This controller is
// the history list + detail, plus the dedicated New-adjustment form (which also captures unit
// cost and expiry per line for received goods).
public class AdjustmentsController : InventoryAreaController
{
    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;
    private readonly IStoreAccessService _access;
    private const int PageSize = 30;

    public AdjustmentsController(ApplicationDbContext db, IStockService stock, IStoreAccessService access)
    {
        _db = db;
        _stock = stock;
        _access = access;
    }

    // ── History list, filterable by date range, reason and branch. ──────────────────────────────
    public async Task<IActionResult> Index(DateTime? from = null, DateTime? to = null,
        string? reason = null, int? storeId = null, int page = 1)
    {
        ViewData["Title"] = "Stock adjustments";

        var q = _db.StockAdjustments.AsQueryable();
        if (from.HasValue) { var f = DateTime.SpecifyKind(from.Value.Date, DateTimeKind.Utc); q = q.Where(a => a.CreatedAt >= f); }
        if (to.HasValue) { var t = DateTime.SpecifyKind(to.Value.Date, DateTimeKind.Utc).AddDays(1); q = q.Where(a => a.CreatedAt < t); }
        if (!string.IsNullOrWhiteSpace(reason)) q = q.Where(a => a.Reason == reason);
        if (storeId.HasValue) q = q.Where(a => a.StoreId == storeId.Value);

        var total = await q.CountAsync();
        var rows = await q.OrderByDescending(a => a.Id)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .Select(a => new AdjustmentListRow
            {
                Id = a.Id,
                Number = a.AdjustmentNumber,
                Store = a.Store.Name.Replace("Sterlin Glams ", ""),
                Reason = a.Reason,
                Source = a.Source,
                Lines = a.Lines.Count,
                NetUnits = a.Lines.Sum(l => (int?)l.QtyDelta) ?? 0,
                When = a.CreatedAt,
                By = a.CreatedByUserId == null ? null
                    : _db.Users.Where(u => u.Id == a.CreatedByUserId).Select(u => u.Email).FirstOrDefault()
            }).ToListAsync();

        ViewBag.Stores = await _db.Stores.OrderBy(s => s.Name).ToListAsync();
        ViewBag.Reasons = AdjustmentReasons.All;
        ViewBag.From = from; ViewBag.To = to; ViewBag.Reason = reason; ViewBag.StoreId = storeId;
        ViewBag.Page = page; ViewBag.TotalPages = (int)Math.Ceiling(total / (double)PageSize); ViewBag.Total = total;
        return View(rows);
    }

    // ── Single record: header + lines. ──────────────────────────────────────────────────────────
    public async Task<IActionResult> Detail(int id)
    {
        var a = await _db.StockAdjustments
            .Include(x => x.Store)
            .Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (a == null) return NotFound();

        if (a.CreatedByUserId != null)
            ViewBag.CreatedBy = await _db.Users.Where(u => u.Id == a.CreatedByUserId)
                .Select(u => u.Email).FirstOrDefaultAsync();
        ViewData["Title"] = a.AdjustmentNumber;
        return View(a);
    }

    // ── Dedicated New-adjustment form. ──────────────────────────────────────────────────────────
    [HttpGet]
    public async Task<IActionResult> Create()
    {
        ViewData["Title"] = "New adjustment";
        await PopulateFormAsync();
        return View();
    }

    public class CreateLine
    {
        public int ProductId { get; set; }
        public int? VariantId { get; set; }
        public int QtyDelta { get; set; }
        public decimal? UnitCost { get; set; }
        public DateTime? ExpiryDate { get; set; }
    }
    public class CreateRequest
    {
        public int StoreId { get; set; }
        public string Reason { get; set; } = "";
        public string? Note { get; set; }
        public List<CreateLine> Lines { get; set; } = new();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([FromBody] CreateRequest req)
    {
        if (req == null) return Json(new { success = false, message = "Nothing submitted." });

        var reason = AdjustmentReasons.All.Contains(req.Reason) ? req.Reason : "Correction";
        var lines = (req.Lines ?? new()).Where(l => l.QtyDelta != 0).ToList();
        if (lines.Count == 0) return Json(new { success = false, message = "Add at least one line with a non-zero quantity." });

        var store = await _db.Stores.FirstOrDefaultAsync(s => s.Id == req.StoreId && s.IsActive);
        if (store == null) return Json(new { success = false, message = "Pick a valid branch." });

        if (!await _access.CanWriteAsync(User, req.StoreId))
            return Json(new { success = false, message = "You can only adjust stock for your assigned branch(es)." });

        // Validate products + any variant ids belong to their product.
        var pids = lines.Select(l => l.ProductId).Distinct().ToList();
        var products = await _db.Products.Where(p => pids.Contains(p.Id))
            .Select(p => new { p.Id, p.Name }).ToDictionaryAsync(p => p.Id, p => p.Name);
        var vids = lines.Where(l => l.VariantId.HasValue).Select(l => l.VariantId!.Value).Distinct().ToList();
        var variants = vids.Count == 0 ? new Dictionary<int, (int ProductId, string Name)>()
            : (await _db.ProductVariants.Where(v => vids.Contains(v.Id))
                .Select(v => new { v.Id, v.ProductId, v.Name }).ToListAsync())
                .ToDictionary(v => v.Id, v => (v.ProductId, v.Name));

        var valid = lines.Where(l => products.ContainsKey(l.ProductId)
            && (l.VariantId == null || (variants.TryGetValue(l.VariantId.Value, out var vv) && vv.ProductId == l.ProductId)))
            .ToList();
        if (valid.Count == 0) return Json(new { success = false, message = "No valid lines." });

        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var type = AdjustmentReasons.MovementType(reason);

        await using var tx = await _db.Database.BeginTransactionAsync();

        // Lock the affected rows (same fixed order as the grid/POS) to avoid racing a concurrent
        // sale/edit on the same cell.
        if (_db.Database.IsNpgsql())
            foreach (var pid in valid.Select(l => l.ProductId).Distinct().OrderBy(x => x))
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM \"StoreInventories\" WHERE \"ProductId\" = {pid} AND \"StoreId\" = {req.StoreId} FOR UPDATE");

        var header = new StockAdjustment
        {
            AdjustmentNumber = $"BSA{await NextSeqAsync():D5}",
            StoreId = req.StoreId,
            Reason = reason,
            Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),
            Source = "Form",
            CreatedByUserId = userId,
            CreatedAt = DateTime.UtcNow
        };

        foreach (var l in valid)
        {
            var balance = await _stock.ApplyAsync(l.ProductId, l.VariantId, req.StoreId, l.QtyDelta, type,
                header.AdjustmentNumber, note: reason, userId: userId, materializeVariant: l.VariantId.HasValue);
            header.Lines.Add(new StockAdjustmentLine
            {
                ProductId = l.ProductId,
                ProductVariantId = l.VariantId,
                ProductName = products[l.ProductId],
                VariantName = l.VariantId.HasValue ? variants[l.VariantId.Value].Name : null,
                QtyDelta = l.QtyDelta,
                BalanceAfter = balance,
                UnitCost = l.UnitCost.HasValue && l.UnitCost.Value > 0 ? l.UnitCost : null,
                ExpiryDate = l.ExpiryDate.HasValue ? DateTime.SpecifyKind(l.ExpiryDate.Value.Date, DateTimeKind.Utc) : null
            });
        }

        _db.StockAdjustments.Add(header);
        try
        {
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Json(new { success = false, message = "Stock levels changed while saving. Please refresh and try again." });
        }

        await LogAsync("Create", "Inventory", header.Id.ToString(),
            $"Stock adjustment {header.AdjustmentNumber} ({reason}) — {header.Lines.Count} line(s)");
        return Json(new { success = true, id = header.Id, number = header.AdjustmentNumber });
    }

    // Autocomplete for the form's product picker.
    [HttpGet]
    public async Task<IActionResult> ProductSearch(string q)
    {
        q = (q ?? "").Trim();
        if (q.Length < 2) return Json(Array.Empty<object>());

        var matches = await _db.Products
            .Where(p => p.IsActive && (EF.Functions.ILike(p.Name, $"%{q}%")
                     || EF.Functions.ILike(p.Sku ?? "", $"%{q}%")
                     || EF.Functions.ILike(p.Barcode ?? "", $"%{q}%")))
            .OrderBy(p => p.Name).Take(15)
            .Select(p => new
            {
                id = p.Id,
                name = p.Name,
                sku = p.Sku,
                variants = p.Variants.Where(v => v.IsActive)
                    .OrderBy(v => v.Name).Select(v => new { id = v.Id, name = v.Name }).ToList()
            }).ToListAsync();
        return Json(matches);
    }

    private async Task PopulateFormAsync()
    {
        var writable = await _access.WritableStoreIdsAsync(User);
        var stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        // Only branches the user can write to are selectable.
        ViewBag.Stores = stores.Where(s => writable.Contains(s.Id))
            .Select(s => new SelectListItem(s.Name.Replace("Sterlin Glams ", ""), s.Id.ToString())).ToList();
        ViewBag.Reasons = AdjustmentReasons.All;
    }

    private async Task<int> NextSeqAsync()
    {
        var last = await _db.StockAdjustments.OrderByDescending(a => a.Id)
            .Select(a => a.AdjustmentNumber).FirstOrDefaultAsync();
        return last != null && last.StartsWith("BSA") && int.TryParse(last[3..], out var p) ? p + 1 : 1;
    }
}

public class AdjustmentListRow
{
    public int Id { get; set; }
    public string Number { get; set; } = "";
    public string Store { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Source { get; set; } = "";
    public int Lines { get; set; }
    public int NetUnits { get; set; }
    public DateTime When { get; set; }
    public string? By { get; set; }
}
