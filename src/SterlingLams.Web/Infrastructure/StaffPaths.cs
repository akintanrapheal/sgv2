namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Configurable URL prefixes for the staff backends so they can be hidden behind unguessable paths
/// (defense-in-depth — the areas are already auth-protected). Read once at startup from config
/// (<c>StaffPaths:Admin</c>, <c>StaffPaths:Inventory</c>, <c>StaffPaths:Marketing</c>, set as Render
/// environment variables <c>StaffPaths__Admin</c> etc.). Unset → falls back to the current names, so
/// the feature is opt-in and safe: nothing changes until a secret value is configured.
///
/// The prefixes drive the area route patterns (so every <c>asp-area</c> link auto-resolves), the
/// bare-root redirects, and the staff-path checks in the CSP / maintenance / staff-detection code.
/// </summary>
public static class StaffPaths
{
    public static string Admin { get; private set; } = "Admin";
    public static string Inventory { get; private set; } = "Inventory";
    public static string Marketing { get; private set; } = "Marketing";
    public static string Pos { get; private set; } = "Pos";

    public static void Init(IConfiguration config)
    {
        Admin     = Clean(config["StaffPaths:Admin"],     "Admin");
        Inventory = Clean(config["StaffPaths:Inventory"], "Inventory");
        Marketing = Clean(config["StaffPaths:Marketing"], "Marketing");
        Pos       = Clean(config["StaffPaths:Pos"],       "Pos");
    }

    /// <summary>True when POS has been moved behind a secret prefix (a custom StaffPaths:Pos is set).</summary>
    public static bool PosIsSecret => !Pos.Equals("Pos", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the staff area has been moved behind a secret prefix. Legacy redirects must
    /// 404 rather than send a Location header containing a secret path — an unauthenticated request
    /// would otherwise be told exactly where the hidden backend lives.</summary>
    public static bool AdminIsSecret     => !Admin.Equals("Admin", StringComparison.OrdinalIgnoreCase);
    public static bool InventoryIsSecret => !Inventory.Equals("Inventory", StringComparison.OrdinalIgnoreCase);
    public static bool MarketingIsSecret => !Marketing.Equals("Marketing", StringComparison.OrdinalIgnoreCase);

    /// <summary>Normalises a configured prefix to a single clean path segment (no slashes/spaces).</summary>
    private static string Clean(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var seg = value.Trim().Trim('/').Trim();
        return string.IsNullOrWhiteSpace(seg) ? fallback : seg;
    }

    // Staff paths that are never secret-prefixed (legacy till, the /me account page).
    private static readonly string[] FixedStaffSegments = { "Till", "me" };

    /// <summary>
    /// True if a path (e.g. a login ReturnUrl) targets a staff backend — respecting the configured
    /// secret prefixes, so it works whether or not custom StaffPaths are set. Staff sign-in must use
    /// this instead of hard-coded "/Admin" checks: once the areas move behind secret prefixes a literal
    /// check fails and the login falls back to the storefront chrome.
    /// </summary>
    public static bool IsStaffPath(string? path)
    {
        var seg = FirstSegment(path);
        if (seg.Length == 0) return false;
        if (Eq(seg, Admin) || Eq(seg, Inventory) || Eq(seg, Marketing) || Eq(seg, Pos)) return true;
        foreach (var f in FixedStaffSegments) if (Eq(seg, f)) return true;
        return false;
    }

    /// <summary>Friendly workspace label for the staff sign-in chrome, based on the target path.</summary>
    public static string WorkspaceLabel(string? path)
    {
        var seg = FirstSegment(path);
        if (Eq(seg, Marketing)) return "Marketing Hub";
        if (Eq(seg, Inventory)) return "Inventory System";
        if (Eq(seg, Pos)) return "Point of Sale";
        return "Staff Workspace";
    }

    /// <summary>First path segment, ignoring the leading slash and any query/fragment.</summary>
    private static string FirstSegment(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var p = path.TrimStart('/');
        var end = p.IndexOfAny(new[] { '/', '?', '#' });
        return end < 0 ? p : p[..end];
    }

    private static bool Eq(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}
