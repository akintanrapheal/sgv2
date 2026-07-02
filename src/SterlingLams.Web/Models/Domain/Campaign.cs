using System.ComponentModel.DataAnnotations;

namespace SterlingLams.Web.Models.Domain;

/// <summary>Which set of recipients a campaign targets (resolved at send time).</summary>
public enum CampaignAudience
{
    AllCustomers,           // everyone who has placed a paid order
    NewsletterSubscribers,  // the newsletter list
    RecentBuyers,           // paid order within AudienceDays
    LapsedCustomers,        // bought before, but nothing within AudienceDays
    NeverOrdered,           // registered customers with no orders
    HighValue,              // lifetime paid spend >= AudienceMinSpend
    ByState                 // most recent delivery state == AudienceState
}

public enum CampaignStatus { Draft, Scheduled, Sending, Sent, Failed }

public enum CampaignRecipientStatus { Pending, Sent, Failed, Skipped }

/// <summary>A one-off marketing email blast to a chosen audience/segment.</summary>
public class Campaign
{
    public int Id { get; set; }

    [MaxLength(150)]
    public string Name { get; set; } = string.Empty;
    [MaxLength(200)]
    public string Subject { get; set; } = string.Empty;
    /// <summary>Sanitised HTML body (inner content; wrapped in the branded email shell on send).</summary>
    public string BodyHtml { get; set; } = string.Empty;

    /// <summary>Optional saved segment. When set, its definition overrides the inline audience fields.</summary>
    public int? SegmentId { get; set; }
    public Segment? Segment { get; set; }

    public CampaignAudience Audience { get; set; } = CampaignAudience.AllCustomers;
    public int? AudienceDays { get; set; }          // RecentBuyers / LapsedCustomers
    public decimal? AudienceMinSpend { get; set; }  // HighValue
    [MaxLength(80)]
    public string? AudienceState { get; set; }      // ByState

    // Optional per-recipient auto-coupon: a unique single-use discount code minted for each
    // recipient and dropped in where {{coupon}} appears in the body (or appended if absent).
    public bool CouponEnabled { get; set; }
    public DiscountType CouponType { get; set; } = DiscountType.Percentage;
    public decimal CouponValue { get; set; }
    public int CouponExpiryDays { get; set; } = 14;
    public decimal? CouponMinOrder { get; set; }

    public CampaignStatus Status { get; set; } = CampaignStatus.Draft;
    /// <summary>When to start sending (UTC). "Send now" sets this to now.</summary>
    public DateTime? ScheduledAt { get; set; }
    public DateTime? SentAt { get; set; }

    public int RecipientCount { get; set; }
    public int SentCount { get; set; }
    public int FailedCount { get; set; }

    public string? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<CampaignRecipient> Recipients { get; set; } = new List<CampaignRecipient>();
}

public class CampaignRecipient
{
    public int Id { get; set; }
    public int CampaignId { get; set; }
    public Campaign? Campaign { get; set; }

    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;
    [MaxLength(200)]
    public string? Name { get; set; }
    public string? UserId { get; set; }

    public CampaignRecipientStatus Status { get; set; } = CampaignRecipientStatus.Pending;
    public DateTime? SentAt { get; set; }
    [MaxLength(300)]
    public string? Error { get; set; }
}

/// <summary>Emails that have opted out of marketing — honoured by every audience resolution.</summary>
public class MarketingSuppression
{
    public int Id { get; set; }
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;  // stored lowercase
    [MaxLength(120)]
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
