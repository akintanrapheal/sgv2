using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SterlingLams.Web.Data;
using SterlingLams.Web.Infrastructure;

namespace SterlingLams.Web.Services;

/// <summary>One image that a customer sees, with the resolution actually delivered to the storefront.</summary>
public record ImageAuditItem(int ProductId, string ProductName, string Label, string Url, int Width, int Height, string Severity);

public class ImageAuditResult
{
    public List<ImageAuditItem> Flagged { get; set; } = new();
    public int Checked { get; set; }
    public int Total { get; set; }
    public int Unreadable { get; set; }
    public bool Partial { get; set; }
    public int MinWidth { get; set; }
    public long ElapsedMs { get; set; }
}

public interface IImageAuditService
{
    /// <summary>Fetches every active product + variant image at the size the product page requests and
    /// flags any whose DELIVERED width is below <paramref name="minWidth"/> — i.e. the original upload is
    /// too small, so it looks soft (the pipeline never upscales). Bounded by time + image count so an
    /// admin request can't run away.</summary>
    Task<ImageAuditResult> ScanAsync(int minWidth = 1000, int maxImages = 4000, CancellationToken ct = default);
}

public class ImageAuditService : IImageAuditService
{
    // The product-detail main image requests this width (Detail.cshtml → Img.Cld(url, 1000) → 1280 bucket),
    // so measuring at the same width tells us exactly what the customer gets.
    private const int RequestWidth = 1280;
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

    public async Task<ImageAuditResult> ScanAsync(int minWidth = 1000, int maxImages = 4000, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new ImageAuditResult { MinWidth = minWidth };

        var products = await _db.Products
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .Select(p => new
            {
                p.Id,
                p.Name,
                Images = p.Images.OrderByDescending(i => i.IsPrimary).ThenBy(i => i.SortOrder).Select(i => i.Url).ToList(),
                Variants = p.Variants.Where(v => v.IsActive && v.ImageUrl != null && v.ImageUrl != "")
                    .Select(v => new { v.Name, Url = v.ImageUrl! }).ToList(),
            })
            .ToListAsync(ct);

        // Flatten to the distinct set of (product, label, source URL) we need to measure. De-dup by URL per
        // product so a variant that reuses the main photo isn't fetched twice.
        var targets = new List<(int ProductId, string ProductName, string Label, string RawUrl)>();
        foreach (var p in products)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < p.Images.Count; i++)
            {
                var url = p.Images[i];
                if (string.IsNullOrWhiteSpace(url) || !seen.Add(url)) continue;
                targets.Add((p.Id, p.Name, i == 0 ? "Main image" : $"Image {i + 1}", url));
            }
            foreach (var v in p.Variants)
            {
                if (!seen.Add(v.Url)) continue;
                targets.Add((p.Id, p.Name, $"Colour: {v.Name}", v.Url));
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
                    flagged.Add(new ImageAuditItem(t.ProductId, t.ProductName, t.Label, t.RawUrl, w, h, severity));
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

    /// <summary>Fetches the image at the storefront's request width and reads its pixel size from the file
    /// header only (a 64 KB range is enough for JPEG/PNG/WebP), falling back to a full fetch if the server
    /// ignores Range or the header spans further.</summary>
    private async Task<(int Width, int Height)> MeasureAsync(string rawUrl, CancellationToken ct)
    {
        var delivered = Img.Cld(rawUrl, RequestWidth) ?? rawUrl;
        if (string.IsNullOrWhiteSpace(delivered) || !delivered.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return (0, 0); // local/placeholder — can't measure

        var client = _httpFactory.CreateClient("imageaudit");

        using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        reqCts.CancelAfter(PerRequest);

        // 1) Header-only range fetch.
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, delivered);
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

        // 2) Full fetch fallback.
        using var reqCts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
        reqCts2.CancelAfter(PerRequest);
        var bytes = await client.GetByteArrayAsync(delivered, reqCts2.Token).ConfigureAwait(false);
        using var ms2 = new MemoryStream(bytes);
        var info2 = Image.Identify(ms2);
        return info2 != null ? (info2.Width, info2.Height) : (0, 0);
    }
}
