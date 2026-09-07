namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// One storefront page view, recorded for the Admin → Traffic dashboard. Deliberately minimal and
/// privacy-light: no raw IPs (only a per-day <see cref="VisitorKey"/> hash for unique-visitor counts),
/// no personal data. Written asynchronously off the request path by <c>TrafficRecorder</c>.
/// </summary>
public class TrafficHit
{
    public long Id { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Request path (query string stripped), e.g. "/Products".</summary>
    public string Path { get; set; } = "";

    /// <summary>Salted hash of IP+UA+day — lets us count distinct visitors per day without storing PII.</summary>
    public string? VisitorKey { get; set; }

    /// <summary>External referring host (traffic source), e.g. "google.com". Null for direct/internal.</summary>
    public string? RefererHost { get; set; }

    /// <summary>Coarse device class from the User-Agent: Desktop | Mobile | Tablet | Bot.</summary>
    public string Device { get; set; } = "Desktop";
}
