namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// In-memory, per-store tally of INTERNAL API/AJAX calls in small time buckets, for the live
/// "Calls per store" chart on the Subscribe page. Singleton; thread-safe; rolling ~5-minute window.
/// Counts real requests (POS calls attributed to the till's branch, storefront/other to "Online").
/// Resets on restart — it's a live activity view, not a historical metric.
/// </summary>
public sealed class ApiActivityTracker
{
    public const int BucketSeconds = 5;      // one point every 5s
    public const int WindowBuckets = 60;     // last 5 minutes

    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<long, int>> _data = new();

    private static long Bucket() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / BucketSeconds;

    public void Record(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) label = "Other";
        var b = Bucket();
        lock (_gate)
        {
            if (!_data.TryGetValue(label, out var d)) { d = new Dictionary<long, int>(); _data[label] = d; }
            d[b] = d.TryGetValue(b, out var c) ? c + 1 : 1;
            if (d.Count > WindowBuckets * 3)   // occasional prune of old buckets
            {
                var cut = b - WindowBuckets;
                foreach (var k in d.Keys.Where(k => k < cut).ToList()) d.Remove(k);
            }
        }
    }

    public ApiActivitySnapshot Snapshot()
    {
        var now = Bucket();
        var start = now - WindowBuckets + 1;
        lock (_gate)
        {
            var buckets = new long[WindowBuckets];
            for (int i = 0; i < WindowBuckets; i++) buckets[i] = (start + i) * BucketSeconds;

            var series = _data.Select(kv =>
            {
                var arr = new int[WindowBuckets];
                var total = 0;
                for (int i = 0; i < WindowBuckets; i++)
                    if (kv.Value.TryGetValue(start + i, out var c)) { arr[i] = c; total += c; }
                return new ApiActivitySeries(kv.Key, total, arr);
            })
            .Where(s => s.Total > 0)
            .OrderByDescending(s => s.Total)
            .ToList();

            return new ApiActivitySnapshot(BucketSeconds, buckets, series, series.Sum(s => s.Total));
        }
    }
}

public record ApiActivitySeries(string Label, int Total, int[] Series);
public record ApiActivitySnapshot(int BucketSeconds, long[] Buckets, IReadOnlyList<ApiActivitySeries> Stores, int Total);
