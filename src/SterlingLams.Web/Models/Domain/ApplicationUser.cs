using Microsoft.AspNetCore.Identity;

namespace SterlingLams.Web.Models.Domain;

public class ApplicationUser : IdentityUser
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public override string? PhoneNumber { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }

    /// <summary>Hashed PIN for quick till sign-in. Null = this user can't sign in at a till.</summary>
    public string? PinHash { get; set; }

    /// <summary>True for shell accounts created automatically during guest checkout (random password,
    /// can't sign in until they reset it). Guest checkout reuses a guest account for the same email
    /// but never attaches an order to a real registered account it doesn't own.</summary>
    public bool IsGuest { get; set; }

    /// <summary>When true, an administrator has revoked this account's access — it is blocked from
    /// signing in (front or back office) until an admin restores it.</summary>
    public bool AccessRevoked { get; set; }

    /// <summary>Free-text admin tags (comma-separated), e.g. "vip, wholesale". For CRM filtering only.</summary>
    public string? Tags { get; set; }

    /// <summary>The customer's own refer-a-friend code (generated on first use). Unique.</summary>
    public string? ReferralCode { get; set; }

    public string FullName => $"{FirstName} {LastName}".Trim();

    public ICollection<Order> Orders { get; set; } = new List<Order>();
    public ICollection<WishlistItem> WishlistItems { get; set; } = new List<WishlistItem>();
    public ICollection<Address> Addresses { get; set; } = new List<Address>();
}
