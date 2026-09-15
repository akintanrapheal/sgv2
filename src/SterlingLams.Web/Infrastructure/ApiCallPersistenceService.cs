using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Flushes the in-memory daily API-call deltas from <see cref="ApiActivityTracker"/> into the
/// ApiCallDaily table every minute (and once on shutdown), via an atomic upsert. Batching keeps it to
/// a handful of writes a minute instead of one per request. Powers the historical calls chart.
/// </summary>
public class ApiCallPersistenceService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private readonly IServiceProvider _sp;
    private readonly ApiActivityTracker _tracker;
    private readonly ILogger<ApiCallPersistenceService> _logger;

    public ApiCallPersistenceService(IServiceProvider sp, ApiActivityTracker tracker, ILogger<ApiCallPersistenceService> logger)
    {
        _sp = sp;
        _tracker = tracker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(Interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
            await FlushAsync(CancellationToken.None);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await FlushAsync(cancellationToken);   // don't lose the last minute on shutdown/redeploy
        await base.StopAsync(cancellationToken);
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        var pending = _tracker.DrainPending();
        if (pending.Count == 0) return;
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        try
        {
            foreach (var (day, label, count) in pending)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO ""ApiCallDaily"" (""Day"", ""Label"", ""Count"")
                    VALUES ({day}, {label}, {count})
                    ON CONFLICT (""Day"", ""Label"")
                    DO UPDATE SET ""Count"" = ""ApiCallDaily"".""Count"" + EXCLUDED.""Count""", ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "API-call daily flush failed; {Count} delta rows dropped this cycle.", pending.Count);
        }

        // Mirror these same recorded calls to Zephiel so its usage matches this chart (best-effort).
        try { await MirrorToZephielAsync(scope, db, pending, ct); }
        catch (Exception ex) { _logger.LogDebug(ex, "Zephiel usage mirror skipped."); }
    }

    // A sentinel store id for storefront ("Online") traffic, which has no real SG store. Provisioned on
    // Zephiel once as its own store so the account total equals this chart's total (POS tills + Online).
    private const int OnlineStoreId = 0;
    private const string OnlineStoreName = "Online / Storefront";
    private const int MaxPingsPerFlush = 1000;   // safety bound on outbound pings per cycle

    private async Task MirrorToZephielAsync(IServiceScope scope, ApplicationDbContext db,
        List<(DateOnly Day, string Label, int Count)> pending, CancellationToken ct)
    {
        var zephiel = scope.ServiceProvider.GetService<SterlingLams.Web.Services.IZephielClient>();
        if (zephiel == null || !await zephiel.IsConfiguredAsync()) return;

        // Sum the drained deltas by label (the day doesn't matter — Zephiel timestamps "now").
        var byLabel = pending.GroupBy(p => p.Label)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Count), StringComparer.OrdinalIgnoreCase);
        if (byLabel.Count == 0) return;

        // Map each label to a Zephiel store: a real store by name, or the synthetic Online store.
        var stores = await db.Stores.Select(s => new { s.Id, s.Name }).ToListAsync(ct);
        var idByName = stores.ToDictionary(s => s.Name, s => s.Id, StringComparer.OrdinalIgnoreCase);

        var budget = MaxPingsPerFlush;
        foreach (var (label, count) in byLabel)
        {
            if (count <= 0 || budget <= 0) continue;
            int storeId;
            if (idByName.TryGetValue(label, out var sid)) storeId = sid;
            else
            {
                // Storefront/unmapped → the Online store. Provision its key once (idempotent).
                if (await zephiel.ProvisionStoreKeyAsync(OnlineStoreId, OnlineStoreName, ct: ct) == null) continue;
                storeId = OnlineStoreId;
            }

            var n = Math.Min(count, budget);
            budget -= n;
            // One gateway hit per recorded call (Zephiel counts one usage event each). Fire the batch
            // and await it inside the scope so the client isn't disposed mid-request.
            var tasks = new List<Task>(n);
            for (var i = 0; i < n; i++)
                tasks.Add(zephiel.NotifyCallAsync(storeId, "/multistore/sync", "POST", 200, ct));
            await Task.WhenAll(tasks);
        }
    }
}
