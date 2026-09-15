using Microsoft.EntityFrameworkCore;
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
        try
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
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
    }
}
