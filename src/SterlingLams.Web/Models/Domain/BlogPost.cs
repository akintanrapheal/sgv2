using System.ComponentModel.DataAnnotations;

namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// A Journal (blog / lookbook) article — long-form content for SEO + storytelling. Body is
/// sanitised HTML authored in the admin rich-text editor; a cover image drives the card + social
/// share. Only <see cref="IsPublished"/> posts are visible on the storefront.
/// </summary>
public class BlogPost
{
    public int Id { get; set; }

    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    /// <summary>URL slug (unique), e.g. /journal/styling-gold-anklets.</summary>
    [MaxLength(220)]
    public string Slug { get; set; } = string.Empty;

    /// <summary>Short teaser for the index cards + the meta-description fallback.</summary>
    [MaxLength(500)]
    public string? Excerpt { get; set; }

    /// <summary>Sanitised HTML article body.</summary>
    public string Body { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? CoverImageUrl { get; set; }

    [MaxLength(120)]
    public string? AuthorName { get; set; }

    public bool IsPublished { get; set; }
    /// <summary>Set the first time the post is published; drives ordering + the article date.</summary>
    public DateTime? PublishedAt { get; set; }

    // Optional SEO overrides (fall back to Title / Excerpt when blank).
    [MaxLength(200)]
    public string? MetaTitle { get; set; }
    [MaxLength(320)]
    public string? MetaDescription { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
