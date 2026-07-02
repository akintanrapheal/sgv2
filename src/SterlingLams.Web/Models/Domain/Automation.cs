using System.ComponentModel.DataAnnotations;

namespace SterlingLams.Web.Models.Domain;

/// <summary>What kicks off an automation. Evaluated by a periodic sweep against existing data, so
/// no hooks are needed in the signup/checkout paths.</summary>
public enum AutomationTrigger
{
    WelcomeNewCustomer, // a customer registers (after the automation was activated)
    PostPurchase,       // a customer's order is paid (after activation)
    WinBackLapsed       // a customer hasn't ordered in WinBackDays
}

public enum AutomationRunStatus { Pending, Sent, Failed, Skipped }

/// <summary>A trigger → (optional delay) → send-email rule. Each customer is enrolled at most once
/// per automation.</summary>
public class Automation
{
    public int Id { get; set; }

    [MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    public AutomationTrigger Trigger { get; set; } = AutomationTrigger.WelcomeNewCustomer;

    /// <summary>For WinBackLapsed: days since the last order before a customer qualifies.</summary>
    public int WinBackDays { get; set; } = 90;

    /// <summary>Wait this many hours after the trigger before sending.</summary>
    public int DelayHours { get; set; }

    [MaxLength(200)]
    public string Subject { get; set; } = string.Empty;
    public string BodyHtml { get; set; } = string.Empty;

    // Optional per-recipient auto-coupon (unique single-use code injected where {{coupon}} appears).
    public bool CouponEnabled { get; set; }
    public DiscountType CouponType { get; set; } = DiscountType.Percentage;
    public decimal CouponValue { get; set; }
    public int CouponExpiryDays { get; set; } = 14;
    public decimal? CouponMinOrder { get; set; }

    public bool IsActive { get; set; }
    /// <summary>When the automation was switched on — the cutoff so it never back-emails historical
    /// customers (welcome/post-purchase only enrol events at/after this time).</summary>
    public DateTime? ActivatedAt { get; set; }

    public int SentCount { get; set; }

    public string? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<AutomationRun> Runs { get; set; } = new List<AutomationRun>();
}

/// <summary>One customer enrolled into an automation (dedupes + tracks the scheduled send).</summary>
public class AutomationRun
{
    public int Id { get; set; }
    public int AutomationId { get; set; }
    public Automation? Automation { get; set; }

    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;
    [MaxLength(200)]
    public string? Name { get; set; }
    public string? UserId { get; set; }

    /// <summary>When the email is due to send (eligibility time + delay).</summary>
    public DateTime RunAt { get; set; }
    public AutomationRunStatus Status { get; set; } = AutomationRunStatus.Pending;
    public DateTime? SentAt { get; set; }
    [MaxLength(300)]
    public string? Error { get; set; }
}
