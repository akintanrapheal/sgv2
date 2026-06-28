using System.ComponentModel.DataAnnotations;

namespace SterlingLams.Web.Models.Domain;

public enum ReferralStatus { Pending, Rewarded, Void }

/// <summary>
/// A refer-a-friend link: one referee is credited to one referrer. Rewarded (loyalty points to
/// both) once the referee places their first paid order.
/// </summary>
public class Referral
{
    public int Id { get; set; }

    public string ReferrerUserId { get; set; } = string.Empty;
    public ApplicationUser? Referrer { get; set; }

    public string RefereeUserId { get; set; } = string.Empty;
    public ApplicationUser? Referee { get; set; }

    /// <summary>The referrer's code that was used (snapshot).</summary>
    [MaxLength(20)]
    public string Code { get; set; } = string.Empty;

    public ReferralStatus Status { get; set; } = ReferralStatus.Pending;

    /// <summary>The referee's first paid order that triggered the reward.</summary>
    public int? QualifyingOrderId { get; set; }
    public int ReferrerPoints { get; set; }
    public int RefereePoints { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RewardedAt { get; set; }
}
