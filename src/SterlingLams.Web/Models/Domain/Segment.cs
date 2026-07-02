using System.ComponentModel.DataAnnotations;

namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// A saved, named audience definition (built on the same rules as the built-in segments). A campaign
/// can target a saved segment instead of re-specifying the audience each time.
/// </summary>
public class Segment
{
    public int Id { get; set; }

    [MaxLength(120)]
    public string Name { get; set; } = string.Empty;
    [MaxLength(300)]
    public string? Description { get; set; }

    public CampaignAudience Audience { get; set; } = CampaignAudience.AllCustomers;
    public int? Days { get; set; }           // RecentBuyers / LapsedCustomers
    public decimal? MinSpend { get; set; }   // HighValue
    [MaxLength(80)]
    public string? State { get; set; }       // ByState

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
