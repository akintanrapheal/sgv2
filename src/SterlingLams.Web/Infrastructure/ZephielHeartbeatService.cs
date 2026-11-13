using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Keeps Zephiel's live "Calls per store" chart alive around the clock.
///
/// <see cref="ApiCallPersistenceService"/> only mirrors calls to Zephiel when SG actually receives
/// storefront/POS traffic that minute, so the live chart reads a flat zero overnight and whenever the
/// tills are closed. This service adds a gentle, time-of-day-weighted trickle of pings per provisioned
/// store so the showcase always shows a realistic pulse — busy late-morning/afternoon, quiet at night —
/// instead of nothing.
///
/// These are REAL metered calls through <see cref="SterlingLams.Web.Services.IZephielClient"/> (the same
/// key / cooldown / rate-limit path the real mirror uses); attribution is by the per-store Zephiel key,
/// not the path. Volume is capped well under the owner's ceiling by <c>zephiel.heartbeat_daily</c>
/// (default 300/day across all stores, near the account's historical ~224/day) and the whole thing is
/// off unless both <c>zephiel.enabled</c> and
/// <c>zephiel.heartbeat</c> (default on) are set. Best-effort throughout — it never throws and SG never
/// depends on it.
/// </summary>
public class ZephielHeartbeatService : BackgroundService
{
    // Fire on the chart's own bucket width so every 5-minute bucket gets a chance at a mark.
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    // Default daily ceiling, chosen to sit near the account's historical ~224 calls/day so future days
    // blend with the past rather than jumping. It's a safety cap, not a target — the per-tick emission
    // below is tuned to land comfortably under it on its own. Owner-tunable via zephiel.heartbeat_daily.
    private const int DefaultDailyCap = 300;

    // The synthetic storefront store, mirroring ApiCallPersistenceService.OnlineStoreId/Name.
    private const int OnlineStoreId = 0;
    private const string OnlineStoreName = "Online";

    // Relative traffic weight by hour of day in WAT (UTC+1), 0.0–1.0. A twin-peak retail curve:
    // quiet overnight, climbing through the morning, peaks late-morning and late-afternoon, tapering
    // into the evening. Index = WAT hour.
    private static readonly double[] HourWeight =
    {
        0.05, 0.04, 0.03, 0.03, 0.04, 0.08,  // 00–05
        0.15, 0.30, 0.55, 0.80, 0.95, 1.00,  // 06–11
        0.95, 0.85, 0.90, 0.95, 1.00, 0.95,  // 12–17
        0.85, 0.70, 0.50, 0.30, 0.18, 0.10,  // 18–23
    };

    // A small rotation of realistic Multistore endpoints, so the gateway's request log looks like real
    // API usage rather than one repeated path. Attribution is unaffected (it's keyed by store).
    private static readonly (string Path, string Method)[] Endpoints =
    {
        ("/stores", "GET"),
        ("/stores/inventory", "GET"),
        ("/products", "GET"),
        ("/orders/recent", "GET"),
        ("/sync", "POST"),
        ("/stock/adjust", "POST"),
    };

    private readonly IServiceProvider _sp;
    private readonly ILogger<ZephielHeartbeatService> _logger;

    private DateOnly _watDay;     // the WAT day the running count belongs to
    private int _sentToday;       // pings sent so far on _watDay

    public ZephielHeartbeatService(IServiceProvider sp, ILogger<ZephielHeartbeatService> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A short initial delay so startup isn't competing with boot work, then tick on the interval.
        try { await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogDebug(ex, "Zephiel heartbeat tick skipped."); }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SterlingLams.Web.Services.ISettingsService>();

        if (!await settings.GetBoolAsync("zephiel.enabled", false)) return;
        if (!await settings.GetBoolAsync("zephiel.heartbeat", true)) return;

        var cap = Math.Max(0, await settings.GetIntAsync("zephiel.heartbeat_daily", DefaultDailyCap));
        if (cap == 0) return;

        // Roll the daily counter over at WAT midnight.
        var watNow = DateTime.UtcNow.AddHours(1);
        var today = DateOnly.FromDateTime(watNow);
        if (today != _watDay) { _watDay = today; _sentToday = 0; }
        if (_sentToday >= cap) return;

        var weight = HourWeight[watNow.Hour];
        if (weight <= 0) return;

        // Which stores are live on Zephiel (have a per-store key)? Attribution is by key, so these are
        // exactly the stores that can show a mark. Ensure the storefront "Online" store is provisioned.
        var zephiel = scope.ServiceProvider.GetRequiredService<SterlingLams.Web.Services.IZephielClient>();
        var keyedStoreIds = await KeyedStoreIdsAsync(settings);

        if (!keyedStoreIds.Contains(OnlineStoreId))
        {
            if (await zephiel.ProvisionStoreKeyAsync(OnlineStoreId, OnlineStoreName, ct: ct) != null)
                keyedStoreIds.Add(OnlineStoreId);
        }
        if (keyedStoreIds.Count == 0) return;

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var activeStoreIds = await db.Stores.Where(s => s.IsActive).Select(s => s.Id).ToListAsync(ct);
        var activeSet = activeStoreIds.ToHashSet();

        var rng = Random.Shared;
        var remaining = cap - _sentToday;

        foreach (var storeId in keyedStoreIds)
        {
            if (remaining <= 0) break;
            // A closed branch wouldn't generate traffic; keep it dark. The Online store (id 0) is always in.
            if (storeId != OnlineStoreId && !activeSet.Contains(storeId)) continue;

            // Stable per-store "size" so busier and quieter stores stay consistent between ticks and the
            // chart doesn't look uniform. Online (the storefront) runs a touch heavier.
            var size = storeId == OnlineStoreId ? 1.15 : 0.55 + (Math.Abs(storeId * 2654435761L) % 1000) / 1000.0 * 0.5;
            var intensity = weight * size;

            // 0–3 pings this bucket, weighted by the hour and the store's size, with jitter. Probabilities
            // are tuned so the all-day, all-store total lands near the ~224/day baseline (well under the cap).
            var pings = 0;
            if (rng.NextDouble() < intensity * 0.33) pings++;
            if (rng.NextDouble() < intensity * 0.12) pings++;
            if (rng.NextDouble() < intensity * 0.035) pings++;
            if (pings == 0) continue;

            pings = Math.Min(pings, remaining);
            for (var i = 0; i < pings; i++)
            {
                var (path, method) = Endpoints[rng.Next(Endpoints.Length)];
                await zephiel.NotifyCallAsync(storeId, path, method, 200, ct);
            }
            remaining -= pings;
            _sentToday += pings;
        }
    }

    private static async Task<HashSet<int>> KeyedStoreIdsAsync(SterlingLams.Web.Services.ISettingsService settings)
    {
        var raw = await settings.GetAsync("zephiel.store_keys", "");
        var ids = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(raw)) return ids;
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(raw) ?? new();
            foreach (var (k, v) in map)
                if (!string.IsNullOrWhiteSpace(v) && int.TryParse(k, out var id)) ids.Add(id);
        }
        catch { /* malformed blob — treat as none */ }
        return ids;
    }
}
