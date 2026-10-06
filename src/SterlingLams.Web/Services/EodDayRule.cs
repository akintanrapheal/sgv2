using System.Linq.Expressions;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

/// <summary>
/// Which day an order belongs to on the End of Day report.
///
/// A counter sale is finished the moment it's rung up, so it counts on its payment date — that is also
/// the cash that has to reconcile to the drawer tonight.
///
/// A website order is different: it becomes a branch's work when someone packs it. One placed yesterday
/// and packed today is today's output, so it counts on the date it was packed — or marked ready for
/// pickup, the other way an order gets fulfilled — and is left out of every EOD until that happens. Its
/// payment date is not used at all, because paying is not the event this report measures.
///
/// This is deliberately NOT how the rest of Finance dates revenue (everything else keys off the paid
/// date), so an EOD total will not tie back to Sales for the same day. That is expected.
/// </summary>
public static class EodDayRule
{
    /// <summary>
    /// Paid orders belonging to the EOD for the half-open UTC window [<paramref name="from"/>,
    /// <paramref name="toExclusive"/>). Kept as one expression so the orders, their line items and their
    /// payment tenders can never be selected by three slightly different versions of the same rule.
    /// </summary>
    public static Expression<Func<Order, bool>> InWindow(DateTime from, DateTime toExclusive) => o =>
        o.IsPaid && (
            o.Channel == OrderChannel.Pos
                ? (o.PaidAt ?? o.CreatedAt) >= from && (o.PaidAt ?? o.CreatedAt) < toExclusive
                : (o.PackedAt ?? o.PickupReadyEmailedAt) != null
                  && (o.PackedAt ?? o.PickupReadyEmailedAt) >= from
                  && (o.PackedAt ?? o.PickupReadyEmailedAt) < toExclusive);
}
