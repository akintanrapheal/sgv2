namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// Persistent daily tally of Meta Conversions API (server-side) events we fired, one row per
/// (day, event name). <see cref="Sent"/> counts every attempt that reached Graph and got a response;
/// <see cref="Accepted"/> counts the ones Meta returned 2xx for. Written in batched flushes (not per
/// event) by <see cref="SterlingLams.Web.Infrastructure.MetaEventPersistenceService"/> from the in-memory
/// <see cref="SterlingLams.Web.Infrastructure.MetaEventTracker"/>, so a storefront flood never turns into
/// a write-per-AddToCart. Powers the "Meta events sent" panel on Admin → Traffic — a send-side
/// confirmation that our server is firing CAPI, distinct from Meta's own attribution in Events Manager.
/// Day is the Lagos (WAT) calendar date, matching the rest of the admin.
/// </summary>
public class MetaEventDaily
{
    public int Id { get; set; }
    public DateOnly Day { get; set; }

    /// <summary>The CAPI event name, e.g. "AddToCart", "InitiateCheckout", "Purchase".</summary>
    public string EventName { get; set; } = "";

    /// <summary>Attempts that reached Meta and got any HTTP response.</summary>
    public int Sent { get; set; }

    /// <summary>Of those, the ones Meta accepted (HTTP 2xx).</summary>
    public int Accepted { get; set; }
}
