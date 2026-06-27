namespace SterlingLams.Web.Models.Domain;

/// <summary>A customer rating + written review for a product. Shown on the storefront only once
/// approved; "verified buyer" means the reviewer has a paid/fulfilled order containing the product.</summary>
public class ProductReview
{
    public int Id { get; set; }

    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;

    /// <summary>The reviewer (a signed-in customer). Reviews require an account.</summary>
    public string? UserId { get; set; }
    /// <summary>Display name snapshot (so it reads correctly even if the account changes).</summary>
    public string AuthorName { get; set; } = string.Empty;

    /// <summary>1–5 stars.</summary>
    public int Rating { get; set; }
    public string? Title { get; set; }
    public string Body { get; set; } = string.Empty;

    /// <summary>Only approved reviews appear on the storefront (moderation).</summary>
    public bool IsApproved { get; set; }
    /// <summary>True when the reviewer has a paid/fulfilled order containing this product.</summary>
    public bool IsVerifiedBuyer { get; set; }

    /// <summary>Optional store reply shown under the review.</summary>
    public string? AdminReply { get; set; }
    public DateTime? RepliedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
