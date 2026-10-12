using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SterlingLams.Web.Data;
using SterlingLams.Web.Infrastructure;

namespace SterlingLams.Web.Services;

/// <summary>One image that a customer sees, with the resolution actually delivered to the storefront.</summary>
public record ImageAuditItem(int ProductId, string ProductName, string Label, string Url, int Width, int Height, string Severity, bool InStock);

public class ImageAuditResult
{
    public List<ImageAuditItem> Flagged { get; set; } = new();
    public int Checked { get; set; }
    public int Total { get; set; }
    public int Unreadable { get; set; }
    public bool Partial { get; set; }
    public int MinWidth { get; set; }
    public string StockFilter { get; set; } = "instock";
    public long ElapsedMs { get; set; }
}

public interface IImageAuditService
{
    /// <summary>Fetches active product + variant images at the size the product page requests and flags any
    /// whose DELIVERED width is below <paramref name="minWidth"/> — i.e. the original upload is too small, so
    /// it looks soft (the pipeline never upscales). <paramref name="stockFilter"/> ("instock" / "outofstock"
    /// / "all") narrows it to products with (or without) sellable stock in a public store, so you can fix the
    /// live catalogue first. Bounded by time + image count so an admin request can't run away.</summary>
    Task<ImageAuditResult> ScanAsync(int minWidth = 1000, int maxImages = 4000, string stockFilter = "instock", CancellationToken ct = default);
}

public class ImageAuditService : IImageAuditService
{
    private const int Concurrency = 16;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan PerRequest = TimeSpan.FromSeconds(10);

    private readonly ApplicationDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ImageAuditService> _log;

    public ImageAuditService(ApplicationDbContext db, IHttpClientFactory httpFactory, ILogger<ImageAuditService> log)
    {
        _db = db;
        _httpFactory = httpFactory;
        _log = log;
    }

    public async Task<ImageAuditResult> ScanAsync(int minWidth = 1000, int maxImages = 4000, string stockFilter = "instock", CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        stockFilter = stockFilter?.ToLowerInvariant() switch { "outofstock" => "outofstock", "all" => "all", _ => "instock" };
        var result = new ImageAuditResult { MinWidth = minWidth, StockFilter = stockFilter };

        // "In stock" = at least one sellable unit (on-hand minus reserved) in a store the storefront shows
        // (active + public) — the same availability the customer sees.
        var publicStoreIds = await _db.Stores.Where(s => s.IsActive && s.IsPublic).Select(s => s.Id).ToListAsync(ct);

        var query = _db.Products.Where(p => p.IsActive).Select(p => new
        {
            p.Id,
            p.Name,
            InStock = _db.StoreInventories.Any(si => si.ProductId == p.Id
                && publicStoreIds.Contains(si.StoreId) && (si.QuantityOnHand - si.QuantityReserved) > 0),
            Images = p.Images.OrderByDescending(i => i.IsPrimary).ThenBy(i => i.SortOrder).Select(i => i.Url).ToList(),
            Variants = p.Variants.Where(v => v.IsActive && v.ImageUrl != null && v.ImageUrl != "")
                .Select(v => new { v.Name, Url = v.ImageUrl! }).ToList(),
        });

        if (stockFilter == "instock") query = query.Where(p => p.InStock);
        else if (stockFilter == "outofstock") query = query.Where(p => !p.InStock);

        var products = await query.OrderBy(p => p.Name).ToListAsync(ct);

        // Flatten to the distinct set of (product, label, source URL) we need to measure. De-dup by URL per
        // product so a variant that reuses the main photo isn't fetched twice.
        var targets = new List<(int ProductId, string ProductName, bool InStock, string Label, string RawUrl)>();
        foreach (var p in products)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < p.Images.Count; i++)
            {
                var url = p.Images[i];
                if (string.IsNullOrWhiteSpace(url) || !seen.Add(url)) continue;
                targets.Add((p.Id, p.Name, p.InStock, i == 0 ? "Main image" : $"Image {i + 1}", url));
            }
            foreach (var v in p.Variants)
            {
                if (!seen.Add(v.Url)) continue;
                targets.Add((p.Id, p.Name, p.InStock, $"Colour: {v.Name}", v.Url));
            }
        }

        result.Total = targets.Count;
        if (targets.Count > maxImages)
        {
            targets = targets.Take(maxImages).ToList();
            result.Partial = true;
        }

        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(Budget);
        var token = budgetCts.Token;

        var gate = new SemaphoreSlim(Concurrency);
        var flagged = new System.Collections.Concurrent.ConcurrentBag<ImageAuditItem>();
        var checkedCount = 0;
        var unreadable = 0;

        var tasks = targets.Select(async t =>
        {
            if (token.IsCancellationRequested) return;
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var (w, h) = await MeasureAsync(t.RawUrl, token).ConfigureAwait(false);
                Interlocked.Increment(ref checkedCount);
                if (w <= 0)
                {
                    Interlocked.Increment(ref unreadable);
                    return;
                }
                if (w < minWidth)
                {
                    var severity = w < 600 ? "Low" : "Soft";
                    flagged.Add(new ImageAuditItem(t.ProductId, t.ProductName, t.Label, t.RawUrl, w, h, severity, t.InStock));
                }
            }
            catch (OperationCanceledException) { /* budget hit — reported as Partial below */ }
            catch (Exception ex)
            {
                Interlocked.Increment(ref unreadable);
                _log.LogDebug(ex, "Image audit could not measure {Url}", t.RawUrl);
            }
            finally { gate.Release(); }
        });

        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { result.Partial = true; }

        if (token.IsCancellationRequested) result.Partial = true;

        result.Checked = checkedCount;
        result.Unreadable = unreadable;
        // Worst (smallest) first, then by product so a product's images group together.
        result.Flagged = flagged.OrderBy(f => f.Width).ThenBy(f => f.ProductName).ToList();
        result.ElapsedMs = sw.ElapsedMilliseconds;
        return result;
    }

    /// <summary>Reads an image's ORIGINAL pixel size from its source bytes (header only — a 64 KB range is
    /// enough for JPEG/PNG/WebP). It fetches the source directly, NOT the storefront delivery URL, so it
    /// never triggers a Cloudflare on-the-fly transform (which would time out with a 524 and burn transform
    /// quota when run across the whole catalogue).</summary>
    private async Task<(int Width, int Height)> MeasureAsync(string rawUrl, CancellationToken ct)
    {
        var measureUrl = OriginalUrl(rawUrl);
        if (string.IsNullOrWhiteSpace(measureUrl) || !measureUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return (0, 0); // local/placeholder — can't measure

        var client = _httpFactory.CreateClient("imageaudit");

        using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        reqCts.CancelAfter(PerRequest);

        // 1) Header-only range fetch.
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, measureUrl);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 65_535);
            using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, reqCts.Token).ConfigureAwait(false);
            if (res.IsSuccessStatusCode)
            {
                await using var stream = await res.Content.ReadAsStreamAsync(reqCts.Token).ConfigureAwait(false);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, reqCts.Token).ConfigureAwait(false);
                ms.Position = 0;
                var info = Image.Identify(ms);
                if (info != null) return (info.Width, info.Height);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* fall through to a full fetch */ }

        // 2) Full fetch fallback — does NOT throw on a non-success status, so a stray 5xx/timeout is counted
        //    as "unreadable" rather than surfacing as an error (and reaching Sentry).
        using var reqCts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
        reqCts2.CancelAfter(PerRequest);
        using var req2 = new HttpRequestMessage(HttpMethod.Get, measureUrl);
        using var res2 = await client.SendAsync(req2, HttpCompletionOption.ResponseHeadersRead, reqCts2.Token).ConfigureAwait(false);
        if (!res2.IsSuccessStatusCode) return (0, 0);
        var bytes = await res2.Content.ReadAsByteArrayAsync(reqCts2.Token).ConfigureAwait(false);
        using var ms2 = new MemoryStream(bytes);
        var info2 = Image.Identify(ms2);
        return info2 != null ? (info2.Width, info2.Height) : (0, 0);
    }

    /// <summary>The ORIGINAL source URL for an image, with any Cloudinary delivery transform stripped (so we
    /// measure the true uploaded resolution, not a baked-in thumbnail) and NOT routed through Cloudflare
    /// transforms. R2 objects and other URLs are already the original and pass through unchanged.</summary>
    private static string OriginalUrl(string url)
    {
        const string marker = "/image/upload/";
        var i = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return url; // not a Cloudinary upload URL (R2 object, local, etc.) — already the original
        var asset = Img.CloudinaryAssetPath(url);
        return string.IsNullOrEmpty(asset) ? url : url[..(i + marker.Length)] + asset;
    }
}
