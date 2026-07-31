namespace SterlingLams.Web.Services;

/// <summary>
/// The calendar every money report runs on.
///
/// Timestamps are stored in UTC, but the business trades in West Africa Time (UTC+1, no DST ever).
/// Reports used <c>DateTime.UtcNow.Date</c>, so a "day" ran 01:00→01:00 Lagos time and anything sold
/// between midnight and 1am counted against the previous day. Every from/to here is a LAGOS calendar
/// date, converted to the UTC instants the database is queried with.
/// </summary>
public static class ReportCalendar
{
    public static readonly TimeZoneInfo Zone = Resolve();

    private static TimeZoneInfo Resolve()
    {
        // IANA on Linux/containers, Windows id for local development, fixed +1 if neither resolves.
        foreach (var id in new[] { "Africa/Lagos", "W. Central Africa Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("WAT", TimeSpan.FromHours(1), "West Africa Time", "WAT");
    }

    /// <summary>A stored UTC timestamp as Lagos wall-clock time.</summary>
    public static DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    /// <summary>The Lagos calendar day a stored UTC timestamp belongs to.</summary>
    public static DateTime LocalDay(DateTime utc) => ToLocal(utc).Date;

    /// <summary>Today's date in Lagos.</summary>
    public static DateTime Today => ToLocal(DateTime.UtcNow).Date;

    /// <summary>Midnight (Lagos) on a local date, expressed as the UTC instant to query with.</summary>
    public static DateTime StartOfDayUtc(DateTime localDate) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localDate.Date, DateTimeKind.Unspecified), Zone);

    /// <summary>
    /// Parses an inclusive from/to pair of Lagos dates (defaulting to the last
    /// <paramref name="defaultDays"/> days) and returns both the local dates — for display — and the
    /// half-open UTC window <c>[FromUtc, ToUtcExclusive)</c> to filter on.
    /// </summary>
    public static (DateTime FromUtc, DateTime ToUtcExclusive, DateTime FromLocal, DateTime ToLocal)
        Range(string? from, string? to, int defaultDays = 30)
    {
        var today = Today;
        var f = DateTime.TryParse(from, out var pf) ? pf.Date : today.AddDays(-(defaultDays - 1));
        var t = DateTime.TryParse(to, out var pt) ? pt.Date : today;
        if (t < f) t = f;
        return (StartOfDayUtc(f), StartOfDayUtc(t.AddDays(1)), f, t);
    }
}
