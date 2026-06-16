namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// A customer's "notify me when back in stock" request for a product. Captured on the product page
/// when the item is out of stock; a background notifier emails the requester once the product is
/// available again and stamps <see cref="NotifiedAt"/> so they're only told once.
/// </summary>
public class BackInStockRequest
{
    public int Id { get; set; }

    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public string Email { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Null = still waiting; set once the back-in-stock email has been sent.</summary>
    public DateTime? NotifiedAt { get; set; }
}
