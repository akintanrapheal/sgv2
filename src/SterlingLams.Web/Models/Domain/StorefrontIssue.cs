namespace SterlingLams.Web.Models.Domain;

/// <summary>The kind of customer-facing problem, for the Customer Issues monitor.</summary>
public enum StorefrontIssueType
{
    AddToCart,    // failed/blocked "add to bag" (out of stock, item error, rejected request)
    Payment,      // payment couldn't start, was declined, or failed at the gateway
    Checkout,     // checkout blocked before payment (sold out at pay, delivery zone/address, timeframe)
    ServerError,  // an unhandled error on a storefront/checkout page (also in Sentry)
    Other         // anything else worth surfacing
}

/// <summary>
/// One problem a customer hit while trying to buy — add-to-cart failures, payment failures, checkout
/// blocks, and unexpected errors. Written (best-effort, never blocking the shopper) at each failure point
/// and shown on Admin → Customer Issues so the owner can see and fix what's costing sales. Server stack
/// traces still live in Sentry; this is the business-facing view.
/// </summary>
public class StorefrontIssue
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public StorefrontIssueType Type { get; set; }
    /// <summary>Short machine-ish code grouping like problems, e.g. "out_of_stock", "paystack_init_failed",
    /// "add_rejected_400", "sold_out_at_checkout". Used for the spike thresholds and filtering.</summary>
    public string Reason { get; set; } = string.Empty;
    /// <summary>Human-readable detail — usually the message the customer saw, or the error summary.</summary>
    public string? Message { get; set; }

    // Context (all optional — filled in where known).
    public int? ProductId { get; set; }
    public string? ProductName { get; set; }
    public int? OrderId { get; set; }
    public string? OrderNumber { get; set; }
    public string? UserId { get; set; }          // signed-in customer, if any
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? Path { get; set; }            // request path
    public string? DeviceType { get; set; }
    public string? IpAddress { get; set; }

    // Triage.
    public bool Resolved { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedByName { get; set; }
}
