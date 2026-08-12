using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.Services;

// Finds and merges DUPLICATE product variants — two (or more) variants of the same product that
// mean the same colour + size written a different way (e.g. "9 / Silver" and "Silver / 9"). These
// make the barcode importer skip rows as "ambiguous". A group is only merged when at most ONE of its
// variants carries real data (stock / a sale / a reservation / a stock-take or transfer line); the
// others must be empty, so deleting them is safe. Any group with two+ "real" variants is SKIPPED for
// manual review (this is what protects genuinely-different mirror designs and malformed combos).
// StoreInventories cascade-delete with the variant; StockMovements / adjustments set-null (history kept).

public class DedupeVariantRow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Barcode { get; set; }
    public int Stock { get; set; }
    public bool Blocking { get; set; }   // has stock / sale / reservation / take / transfer — never deleted
}

public class DedupeGroup
{
    public string Sku { get; set; } = "";
    public string Product { get; set; } = "";
    public string Signature { get; set; } = "";
    public DedupeVariantRow? Keep { get; set; }
    public List<DedupeVariantRow> Delete { get; set; } = new();
    public string? BarcodeMove { get; set; }   // "011674 → 9 / Silver"
    public string? SkipReason { get; set; }     // set when the group is left for manual review
}

public class DedupeResult
{
    public bool Committed { get; set; }
    public List<DedupeGroup> Merges { get; set; } = new();   // safe to merge (or merged)
    public List<DedupeGroup> Skipped { get; set; } = new();   // conflict — left alone
    public int VariantsDeleted { get; set; }
    public int BarcodesMoved { get; set; }
    public List<string> Errors { get; set; } = new();
    public int ProductCount => Merges.Select(m => m.Sku).Distinct().Count();
    public string Summary =>
        $"{Merges.Count} duplicate sets across {ProductCount} products · {Merges.Sum(m => m.Delete.Count)} duplicate variants " +
        $"{(Committed ? "removed" : "to remove")} · {BarcodesMoved} barcodes moved to the kept variant · {Skipped.Count} skipped for review";
}

public class VariantDedupeService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<VariantDedupeService> _log;
    public VariantDedupeService(ApplicationDbContext db, ILogger<VariantDedupeService> log) { _db = db; _log = log; }

    /// <summary>Colour+size signature: the variant name's alphanumeric tokens, lower-cased and sorted,
    /// so "9 / Silver" and "Silver / 9" collapse to the same key.</summary>
    private static string Signature(string? name)
    {
        var toks = Regex.Split((name ?? "").ToLowerInvariant(), "[^a-z0-9]+").Where(t => t.Length > 0).OrderBy(t => t);
        return string.Join(",", toks);
    }

    public async Task<DedupeResult> ScanAsync(bool commit)
    {
        var result = new DedupeResult { Committed = commit };

        // 1) All variants → group by (product, signature); keep only groups with a duplicate.
        var all = await _db.ProductVariants
            .Select(v => new { v.Id, v.ProductId, v.Name, v.Barcode, Sku = v.Product!.Sku, Product = v.Product.Name })
            .ToListAsync();

        var dupGroups = all
            .GroupBy(v => new { v.ProductId, Sig = Signature(v.Name) })
            .Where(g => g.Count() > 1)
            .ToList();
        if (dupGroups.Count == 0) return result;

        // 2) Which candidate variants carry real data? (batched lookups)
        var ids = dupGroups.SelectMany(g => g.Select(v => v.Id)).ToHashSet();

        var withStock = (await _db.StoreInventories
            .Where(si => si.ProductVariantId != null && ids.Contains(si.ProductVariantId.Value))
            .GroupBy(si => si.ProductVariantId!.Value)
            .Select(g => new { Vid = g.Key, Qty = g.Sum(x => x.QuantityOnHand) })
            .ToListAsync()).ToDictionary(x => x.Vid, x => x.Qty);

        async Task<HashSet<int>> Present<T>(IQueryable<T> q, System.Linq.Expressions.Expression<Func<T, int?>> sel)
            => (await q.Select(sel).Where(x => x != null && ids.Contains(x!.Value)).Distinct().ToListAsync())
               .Select(x => x!.Value).ToHashSet();

        var blocking = new HashSet<int>();
        blocking.UnionWith(withStock.Where(kv => kv.Value > 0).Select(kv => kv.Key));
        blocking.UnionWith(await Present(_db.OrderItems, oi => oi.ProductVariantId));
        blocking.UnionWith(await Present(_db.RefundItems, ri => ri.ProductVariantId));
        blocking.UnionWith(await Present(_db.StockReservations, sr => (int?)sr.ProductVariantId));
        blocking.UnionWith(await Present(_db.StockTakeLines, tl => tl.ProductVariantId));
        blocking.UnionWith(await Present(_db.StockTransferItems, ti => (int?)ti.ProductVariantId));

        // 3) Build merge / skip decisions.
        foreach (var g in dupGroups)
        {
            var members = g.Select(v => new DedupeVariantRow
            {
                Id = v.Id, Name = v.Name ?? "", Barcode = v.Barcode,
                Stock = withStock.TryGetValue(v.Id, out var q) ? q : 0,
                Blocking = blocking.Contains(v.Id)
            }).OrderBy(v => v.Id).ToList();

            var grp = new DedupeGroup { Sku = g.First().Sku ?? "", Product = g.First().Product ?? "", Signature = g.Key.Sig };
            var blockers = members.Where(m => m.Blocking).ToList();

            if (blockers.Count > 1)
            {
                grp.SkipReason = $"{blockers.Count} variants have stock or sales — needs manual review";
                grp.Delete = members;   // shown for context, not deleted
                result.Skipped.Add(grp);
                continue;
            }

            // Keep the one real variant, else the one with a barcode, else the lowest Id.
            grp.Keep = blockers.FirstOrDefault()
                       ?? members.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.Barcode))
                       ?? members.First();
            grp.Delete = members.Where(m => m.Id != grp.Keep.Id).ToList();

            // Preserve a barcode: if the kept variant has none but a doomed one does, move it.
            if (string.IsNullOrWhiteSpace(grp.Keep.Barcode))
            {
                var donor = grp.Delete.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.Barcode));
                if (donor != null) grp.BarcodeMove = $"{donor.Barcode} → {grp.Keep.Name}";
            }
            result.Merges.Add(grp);
        }

        // 4) Apply.
        if (commit && result.Merges.Count > 0)
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync();
                foreach (var grp in result.Merges)
                {
                    var keep = await _db.ProductVariants.FirstAsync(v => v.Id == grp.Keep!.Id);

                    if (grp.BarcodeMove != null && string.IsNullOrWhiteSpace(keep.Barcode))
                    {
                        var donor = grp.Delete.First(m => !string.IsNullOrWhiteSpace(m.Barcode));
                        // Null the donor first so the unique barcode index doesn't collide, then move it.
                        var donorV = await _db.ProductVariants.FirstAsync(v => v.Id == donor.Id);
                        donorV.Barcode = null;
                        await _db.SaveChangesAsync();
                        keep.Barcode = donor.Barcode;
                        result.BarcodesMoved++;
                    }

                    var delIds = grp.Delete.Select(d => d.Id).ToList();
                    var toDelete = await _db.ProductVariants.Where(v => delIds.Contains(v.Id)).ToListAsync();
                    _db.ProductVariants.RemoveRange(toDelete);
                    result.VariantsDeleted += toDelete.Count;
                    await _db.SaveChangesAsync();
                }
                await tx.CommitAsync();
            });
        }

        _log.LogInformation("[variant-dedupe] {Mode}: {Summary}", commit ? "COMMIT" : "scan", result.Summary);
        return result;
    }
}
