using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Infrastructure;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Admin.Controllers;

/// <summary>
/// Owner-only "Cap discounts at 15%" tool. Any website sale price or in-store (POS) price — on a product
/// OR a variant — that is currently discounted MORE than 15% off its regular price is raised to exactly
/// 15% off. Items already discounted 15% or less, and items with no discount, are left untouched.
///
/// Why: for a store-wide promo where the cashier applies one 15% TOTAL discount at the till, an item that
/// is already (say) 40% off would stack to ~49%. Capping the base at 15% keeps the combined discount ~30%.
///
/// Preview-first (Index shows every change), Apply writes the change AND a <see cref="PriceCapBackup"/>
/// snapshot of every original value, and Revert restores the latest batch. The bulk save suppresses the
/// label-reprint queue (so it isn't flooded) and evicts the storefront cache so new prices show at once.
/// Section == null → owner-only (see AdminBaseController).
/// </summary>
public class PriceCapController : AdminBaseController
{
    protected override string? Section => null; // owner-only

    private readonly ApplicationDbContext _db;
    private readonly SterlingLams.Web.Services.IStorefrontCache _storefrontCache;

    public PriceCapController(ApplicationDbContext db, SterlingLams.Web.Services.IStorefrontCache storefrontCache)
    {
        _db = db;
        _storefrontCache = storefrontCache;
    }

    // New price at ≤15% off (rounded up to the whole naira). Math lives in PriceCapMath (unit-tested).
    private static decimal NewPrice(decimal regular) => PriceCapMath.NewPrice(regular);

    // Products that MIGHT have something to cap (any sale/pos price set). The exact >15% test is applied in
    // memory by Build() — kept out of SQL so there's no risk of an untranslatable arithmetic/?? expression.
    private IQueryable<Product> Candidates() =>
        _db.Products.Include(p => p.Variants)
            .Where(p => !p.IsCustomItem && p.Price > 0 && (
                p.SalePrice != null || p.PosPrice != null ||
                p.Variants.Any(v => v.SalePrice != null || v.PosPrice != null)));

    // Flatten loaded products into the exact list of fields discounted more than 15% (what will change).
    private static List<CapChange> Build(IEnumerable<Product> products)
    {
        var list = new List<CapChange>();
        foreach (var p in products)
        {
            if (p.SalePrice is decimal ps && PriceCapMath.ShouldCap(p.Price, ps))
                list.Add(new CapChange(p.Id, p.Name, null, null, "SalePrice", p.Price, ps, NewPrice(p.Price)));
            if (p.PosPrice is decimal pp && PriceCapMath.ShouldCap(p.Price, pp))
                list.Add(new CapChange(p.Id, p.Name, null, null, "PosPrice", p.Price, pp, NewPrice(p.Price)));
            foreach (var v in p.Variants)
            {
                var reg = v.Price ?? p.Price;    // a variant's effective regular price
                if (v.SalePrice is decimal vs && PriceCapMath.ShouldCap(reg, vs))
                    list.Add(new CapChange(p.Id, p.Name, v.Id, v.Name, "SalePrice", reg, vs, NewPrice(reg)));
                if (v.PosPrice is decimal vpp && PriceCapMath.ShouldCap(reg, vpp))
                    list.Add(new CapChange(p.Id, p.Name, v.Id, v.Name, "PosPrice", reg, vpp, NewPrice(reg)));
            }
        }
        return list;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Cap discounts at 15%";

        var products = await Candidates().AsNoTracking().ToListAsync();
        var changes = Build(products);

        var lastBatch = await _db.PriceCapBackups.Where(b => !b.Reverted)
            .OrderByDescending(b => b.CreatedAt).FirstOrDefaultAsync();

        var vm = new CapVm
        {
            Changes = changes.OrderByDescending(c => c.OldPct).ToList(),
            ProductsAffected = changes.Select(c => c.ProductId).Distinct().Count(),
            LastAppliedAt = lastBatch?.CreatedAt,
            LastBatchCount = lastBatch == null ? 0
                : await _db.PriceCapBackups.CountAsync(b => b.BatchId == lastBatch.BatchId && !b.Reverted)
        };
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Apply(string confirm)
    {
        if (!string.Equals((confirm ?? "").Trim(), "CAP", StringComparison.Ordinal))
        {
            TempData["Error"] = "Type CAP (in capitals) to confirm.";
            return RedirectToAction(nameof(Index));
        }

        var products = await Candidates().ToListAsync(); // tracked — we mutate these
        var now = DateTime.UtcNow;
        var batch = Guid.NewGuid();
        var backups = new List<PriceCapBackup>();
        var touched = new HashSet<int>();

        void Snap(int pid, int? vid, string field, decimal oldVal, decimal newVal)
        {
            backups.Add(new PriceCapBackup { BatchId = batch, ProductId = pid, ProductVariantId = vid,
                Field = field, OldValue = oldVal, NewValue = newVal, CreatedAt = now });
            touched.Add(pid);
        }

        foreach (var p in products)
        {
            if (p.SalePrice is decimal ps && PriceCapMath.ShouldCap(p.Price, ps))
            { var nv = NewPrice(p.Price); Snap(p.Id, null, "SalePrice", ps, nv); p.SalePrice = nv; }
            if (p.PosPrice is decimal pp && PriceCapMath.ShouldCap(p.Price, pp))
            { var nv = NewPrice(p.Price); Snap(p.Id, null, "PosPrice", pp, nv); p.PosPrice = nv; }
            foreach (var v in p.Variants)
            {
                var reg = v.Price ?? p.Price;
                if (v.SalePrice is decimal vs && PriceCapMath.ShouldCap(reg, vs))
                { var nv = NewPrice(reg); Snap(p.Id, v.Id, "SalePrice", vs, nv); v.SalePrice = nv; }
                if (v.PosPrice is decimal vpp && PriceCapMath.ShouldCap(reg, vpp))
                { var nv = NewPrice(reg); Snap(p.Id, v.Id, "PosPrice", vpp, nv); v.PosPrice = nv; }
            }
            if (touched.Contains(p.Id)) p.UpdatedAt = now;
        }

        if (backups.Count == 0)
        {
            TempData["Success"] = "Nothing to cap — every item is already at 15% off or less.";
            return RedirectToAction(nameof(Index));
        }

        _db.PriceCapBackups.AddRange(backups);

        // A deliberate bulk price change: don't flood the label-reprint queue with every capped item.
        _db.SuppressReprintFlagging = true;
        try { await _db.SaveChangesAsync(); }
        finally { _db.SuppressReprintFlagging = false; }

        try { await _storefrontCache.EvictAsync(); } catch { /* cache is best-effort */ }

        await LogAsync("PriceCap", "Product", null,
            $"Capped {backups.Count} discounted price(s) across {touched.Count} product(s) at 15% off (batch {batch}).");
        TempData["Success"] = $"Done — reduced {backups.Count} over-15% discount(s) across {touched.Count} product(s) to 15% off. You can revert this anytime.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Revert(string confirm)
    {
        if (!string.Equals((confirm ?? "").Trim(), "REVERT", StringComparison.Ordinal))
        {
            TempData["Error"] = "Type REVERT (in capitals) to confirm.";
            return RedirectToAction(nameof(Index));
        }

        var lastBatch = await _db.PriceCapBackups.Where(b => !b.Reverted)
            .OrderByDescending(b => b.CreatedAt).Select(b => (Guid?)b.BatchId).FirstOrDefaultAsync();
        if (lastBatch == null)
        {
            TempData["Error"] = "There's no capped batch to revert.";
            return RedirectToAction(nameof(Index));
        }

        var rows = await _db.PriceCapBackups.Where(b => b.BatchId == lastBatch && !b.Reverted).ToListAsync();
        var pids = rows.Select(r => r.ProductId).Distinct().ToList();
        var byId = (await _db.Products.Include(p => p.Variants).Where(p => pids.Contains(p.Id)).ToListAsync())
            .ToDictionary(p => p.Id);
        var now = DateTime.UtcNow;

        foreach (var r in rows)
        {
            if (!byId.TryGetValue(r.ProductId, out var p)) continue;
            if (r.ProductVariantId == null)
            {
                if (r.Field == "SalePrice") p.SalePrice = r.OldValue;
                else if (r.Field == "PosPrice") p.PosPrice = r.OldValue;
            }
            else
            {
                var v = p.Variants.FirstOrDefault(x => x.Id == r.ProductVariantId);
                if (v == null) continue;
                if (r.Field == "SalePrice") v.SalePrice = r.OldValue;
                else if (r.Field == "PosPrice") v.PosPrice = r.OldValue;
            }
            p.UpdatedAt = now;
            r.Reverted = true;
            r.RevertedAt = now;
        }

        _db.SuppressReprintFlagging = true;
        try { await _db.SaveChangesAsync(); }
        finally { _db.SuppressReprintFlagging = false; }

        try { await _storefrontCache.EvictAsync(); } catch { }

        await LogAsync("PriceCapRevert", "Product", null,
            $"Reverted {rows.Count} capped price(s) across {pids.Count} product(s) (batch {lastBatch}).");
        TempData["Success"] = $"Reverted — restored {rows.Count} original price(s) across {pids.Count} product(s).";
        return RedirectToAction(nameof(Index));
    }

    // One field on one product/variant that is discounted more than 15% (what the preview shows + Apply writes).
    public record CapChange(int ProductId, string ProductName, int? VariantId, string? VariantName,
        string Field, decimal Regular, decimal OldValue, decimal NewValue)
    {
        public decimal OldPct => Regular > 0 ? Math.Round((Regular - OldValue) / Regular * 100m, 1) : 0m;
        public decimal NewPct => Regular > 0 ? Math.Round((Regular - NewValue) / Regular * 100m, 1) : 0m;
        public string FieldLabel => Field == "PosPrice" ? "In-store price" : "Website sale";
    }

    public class CapVm
    {
        public List<CapChange> Changes { get; set; } = new();
        public int ProductsAffected { get; set; }
        public int FieldsAffected => Changes.Count;
        public DateTime? LastAppliedAt { get; set; }
        public int LastBatchCount { get; set; }
    }
}
