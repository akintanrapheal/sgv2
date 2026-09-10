namespace SterlingLams.Web.Models.Domain;

public class Address
{
    public int Id { get; set; }

    public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;

    public string Label { get; set; } = "Home";
    public string FullName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Line1 { get; set; } = string.Empty;
    public string? Line2 { get; set; }
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Country { get; set; } = "Nigeria";
    public string? PostalCode { get; set; }

    public bool IsDefault { get; set; }

    /// <summary>Soft-delete flag: the customer removed this from their address book, but the row is
    /// kept because past orders reference it (FK is Restrict to protect order history). Archived
    /// addresses are hidden from the profile list and checkout.</summary>
    public bool IsArchived { get; set; }
}
