namespace SterlingLams.Web.Models.Domain;

public class Store
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;

    public string? Phone { get; set; }
    public string? Email { get; set; }

    public string? OpeningHours { get; set; }

    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    /// <summary>Operationally active: the branch trades in the EPOS, holds stock, receives transfers and
    /// shows in the backend/reports. Deactivating takes its tills offline and hides it everywhere.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Visible to customers: the branch shows on the website (store locator, pickup options) and
    /// is eligible to fulfil online orders. A branch can be <see cref="IsActive"/> but NOT public — e.g.
    /// one being stocked up (transfers in, EPOS running) before it opens to customers. Default true, so
    /// existing branches are unaffected. Customer-facing = IsActive AND IsPublic.</summary>
    public bool IsPublic { get; set; } = true;

    public ICollection<StoreInventory> Inventories { get; set; } = new List<StoreInventory>();
    public ICollection<Order> Orders { get; set; } = new List<Order>();
}
