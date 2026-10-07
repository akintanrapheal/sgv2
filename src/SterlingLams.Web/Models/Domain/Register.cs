namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// A physical till / point-of-sale terminal, bound to a branch. The till device remembers
/// which Register it is, so every sale is tagged to the right branch automatically.
/// </summary>
public class Register
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    public int StoreId { get; set; }
    public Store Store { get; set; } = null!;

    public bool IsActive { get; set; } = true;

    /// <summary>The store's "online till": website (online-paid) orders are attributed to THIS register's
    /// cash-up only. Exactly one register per store should have this set, so website sales aren't counted
    /// on every open till (which double-counted them across concurrent sessions). Other tills show only
    /// the in-store sales rung up on them. The online till still rings up in-store sales normally.</summary>
    public bool HandlesOnlineOrders { get; set; }
}
