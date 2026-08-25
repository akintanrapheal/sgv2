namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Retries a READ-ONLY database query when it fails with a transient error (Render's Postgres drops
/// connections intermittently — "Timeout during reading attempt"). Safe ONLY for pure reads: the
/// query is simply re-run, so it must not mutate state or sit inside a transaction. For writes /
/// transactions this is NOT safe (re-running would duplicate work) — leave those alone.
/// </summary>
public static class DbRead
{
    public static async Task<T> RetryAsync<T>(Func<Task<T>> read, int attempts = 3)
    {
        for (var i = 1; ; i++)
        {
            try { return await read(); }
            catch (Exception ex) when (i < attempts && IsTransient(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150 * i));
            }
        }
    }

    // EF wraps transient DB failures in InvalidOperationException; the real cause is a TimeoutException
    // or an NpgsqlException flagged transient. Walk the inner-exception chain to find either.
    private static bool IsTransient(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is TimeoutException) return true;
            if (e is Npgsql.NpgsqlException npg && npg.IsTransient) return true;
        }
        return false;
    }
}
