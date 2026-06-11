namespace SterlingLams.Web.Models.Domain;

/// <summary>An email captured from the storefront "Join Our Newsletter" signup.</summary>
public class NewsletterSubscriber
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
