using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Infrastructure.Traffic;

public interface ITrafficRecorder
{
    void Record(TrafficHit hit);
}

/// <summary>
/// Buffers storefront page-view hits in a bounded in-memory channel and flushes them to the database
/// in batches on a background thread. Recording a view therefore never adds latency to the request and
/// never blocks it — if the buffer is full (a traffic spike) the hit is simply dropped. Also prunes
/// hits older than the retention window once a day. Registered as a singleton + hosted service.
/// </summary>
public class TrafficRecorder : BackgroundService, ITrafficRecorder
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<TrafficRecorder> _log;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(120);

    private readonly Channel<TrafficHit> _channel =
        Channel.CreateBounded<TrafficHit>(new BoundedChannelOptions(5000)
        {
            FullMode = BoundedChannelFullMode.DropWrite,   // never block the request thread
            SingleReader = true,
        });

    public TrafficRecorder(IServiceScopeFactory scopes, ILogger<TrafficRecorder> log)
    {
        _scopes = scopes;
        _log = log;
    }

    public void Record(TrafficHit hit) => _channel.Writer.TryWrite(hit);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = _channel.Reader;
        var batch = new List<TrafficHit>(512);
        var lastPrune = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await reader.WaitToReadAsync(stoppingToken)) break;

                batch.Clear();
                while (batch.Count < 500 && reader.TryRead(out var h)) batch.Add(h);

                if (batch.Count > 0)
                {
                    using var scope = _scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    db.TrafficHits.AddRange(batch);
                    await db.SaveChangesAsync(stoppingToken);
                }

                if (DateTime.UtcNow - lastPrune > TimeSpan.FromHours(24))
                {
                    lastPrune = DateTime.UtcNow;
                    using var scope = _scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var cutoff = DateTime.UtcNow - Retention;
                    await db.TrafficHits.Where(t => t.CreatedAt < cutoff).ExecuteDeleteAsync(stoppingToken);
                }

                // Coalesce bursts: flush at most ~every 2s rather than once per hit.
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Traffic flush failed; dropping this batch and backing off.");
                try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }
}
