using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Flushes the in-memory Meta CAPI deltas from <see cref="MetaEventTracker"/> into the MetaEventDaily
/// table every minute (and once on shutdown), via an atomic upsert. Batching keeps it to a handful of
/// writes a minute instead of one per AddToCart. Powers the "Meta events sent" panel on Admin → Traffic.
/// </summary>
public class MetaEventPersistenceService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private readonly IServiceProvider _sp;
    private readonly MetaEventTracker _tracker;
    private readonly ILogger<MetaEventPersistenceService> _logger;

    public MetaEventPersistenceService(IServiceProvider sp, MetaEventTracker tracker,
        ILogger<MetaEventPersistenceService> logger)
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
            foreach (var (day, ev, sent, accepted) in pending)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO ""MetaEventDaily"" (""Day"", ""EventName"", ""Sent"", ""Accepted"")
                    VALUES ({day}, {ev}, {sent}, {accepted})
                    ON CONFLICT (""Day"", ""EventName"")
                    DO UPDATE SET ""Sent"" = ""MetaEventDaily"".""Sent"" + EXCLUDED.""Sent"",
                                  ""Accepted"" = ""MetaEventDaily"".""Accepted"" + EXCLUDED.""Accepted""", ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Meta-event daily flush failed; {Count} delta rows dropped this cycle.", pending.Count);
        }
    }
}
