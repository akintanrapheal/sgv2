namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// In-memory daily tally of Meta Conversions API sends, drained periodically into the MetaEventDaily
/// table by <see cref="MetaEventPersistenceService"/>. Singleton; thread-safe. AddToCart can fire many
/// times a minute, so events are counted in memory and flushed in batches — never a DB write per event
/// (the storefront's Postgres pool is small). Resets on restart; the durable total lives in the table.
/// </summary>
public sealed class MetaEventTracker
{
    private readonly object _gate = new();
    // Un-flushed deltas: (WAT day, event name) → (sent, accepted).
    private readonly Dictionary<(DateOnly Day, string Event), (int Sent, int Accepted)> _pending = new();

    // Lagos (WAT = UTC+1, no DST) calendar date, matching how the admin shows times.
    private static DateOnly WatDay() => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(1));

    /// <summary>Records one CAPI attempt. <paramref name="accepted"/> is true when Meta returned 2xx.</summary>
    public void Record(string eventName, bool accepted)
    {
        if (string.IsNullOrWhiteSpace(eventName)) eventName = "Other";
        var key = (WatDay(), eventName);
        lock (_gate)
        {
            var cur = _pending.TryGetValue(key, out var v) ? v : (0, 0);
            _pending[key] = (cur.Item1 + 1, cur.Item2 + (accepted ? 1 : 0));
        }
    }

    /// <summary>Returns and clears the un-flushed deltas so the persistence service can upsert them.</summary>
    public List<(DateOnly Day, string Event, int Sent, int Accepted)> DrainPending()
    {
        lock (_gate)
        {
            var list = _pending.Select(kv => (kv.Key.Day, kv.Key.Event, kv.Value.Sent, kv.Value.Accepted)).ToList();
            _pending.Clear();
            return list;
        }
    }
}
