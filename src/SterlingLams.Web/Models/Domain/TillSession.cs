namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// A cashier's shift on a register: opened with a cash float, sales are recorded against it,
/// then it's "cashed up" (counted) and closed, producing a Z-report.
/// </summary>
public class TillSession
{
    public int Id { get; set; }

    public int RegisterId { get; set; }
    public Register Register { get; set; } = null!;

    /// <summary>The cashier who opened the session.</summary>
    public string OpenedByUserId { get; set; } = string.Empty;

    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    public decimal OpeningFloat { get; set; }

    public DateTime? ClosedAt { get; set; }
    public string? ClosedByUserId { get; set; }
    public decimal? CountedCash { get; set; }
    public string? ClosingNote { get; set; }

    public bool IsOpen => ClosedAt == null;

    public ICollection<Order> Orders { get; set; } = new List<Order>();
}
