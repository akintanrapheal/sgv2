namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// Assigns a staff user to a branch for store-level authorization. A user with NO rows here is
/// treated as unrestricted (can act on any branch — legacy/default); adding rows narrows them to
/// just those branches. Admins always bypass (all branches). Enforcement is writes-only:
/// reads (stock grid, reports) stay open; mutations are scoped to assigned branches.
/// </summary>
public class UserStore
{
    public int Id { get; set; }

    public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;

    public int StoreId { get; set; }
    public Store Store { get; set; } = null!;
}
