using System;

namespace SterlingLams.Web.Tests;

/// <summary>A fixed clock for the cash-up tests, pinned to mid-day so the ±few-hour session/order times
/// used in the tests never straddle a Lagos-midnight day boundary (which is what made them fail for a
/// ~2-hour window each night against the wall clock). CashUpService takes a TimeProvider for exactly this.</summary>
internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;
    public FixedTimeProvider(DateTimeOffset now) => _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
}

internal static class CashUpTestClock
{
    // 12:00 UTC (13:00 WAT) — comfortably mid Lagos-day, so now-2h … now are all the same WAT date.
    public static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
    public static DateTime NowUtc => Now.UtcDateTime;
    public static readonly TimeProvider Provider = new FixedTimeProvider(Now);
    /// <summary>The Lagos (WAT) calendar date of <see cref="Now"/> — the test equivalent of
    /// ReportCalendar.Today, pinned so day-boundary tests agree with the service's injected clock.</summary>
    public static DateTime TodayLocal => SterlingLams.Web.Services.ReportCalendar.ToLocal(NowUtc).Date;
}
