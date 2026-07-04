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

    public static void Init(IConfiguration config)
    {
        Admin     = Clean(config["StaffPaths:Admin"],     "Admin");
        Inventory = Clean(config["StaffPaths:Inventory"], "Inventory");
        Marketing = Clean(config["StaffPaths:Marketing"], "Marketing");
    }

    /// <summary>Normalises a configured prefix to a single clean path segment (no slashes/spaces).</summary>
    private static string Clean(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var seg = value.Trim().Trim('/').Trim();
        return string.IsNullOrWhiteSpace(seg) ? fallback : seg;
    }
}
