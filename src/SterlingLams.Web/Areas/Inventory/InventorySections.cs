namespace SterlingLams.Web.Areas.Inventory;

/// <summary>One grantable tab of the Inventory System (POS, Sales, CRM, …). Mirrors the sidebar groups
/// in <c>_InventoryLayout</c>. A role can be granted View and/or Manage on each tab from the Inventory
/// System's own "Roles &amp; permissions" screen.</summary>
public record InvSection(string Key, string Label, string Blurb);

/// <summary>
/// The Inventory System's own permission model — SEPARATE from the Website Admin sections.
/// <para>
/// Two ways a role gets in:
/// <list type="bullet">
/// <item><b>Umbrella</b> (<see cref="Umbrella"/> = "InventorySystem") — the "Inventory System" toggle in
/// the Website Admin's Roles &amp; Permissions. Granting it gives access to every tab at once.</item>
/// <item><b>Per-tab</b> (<c>Inv.Pos</c>, <c>Inv.Sales</c>, …) — granted from inside the Inventory
/// System's own Roles screen, for roles that should only see some tabs.</item>
/// </list>
/// Keys use a dot ("Inv.Pos") so they never collide with the colon-separated Admin permission grammar
/// ("&lt;section&gt;:manage"); a Manage grant is still "&lt;key&gt;:manage" (e.g. "Inv.Pos:manage").
/// </para>
/// </summary>
public static class InventorySections
{
    /// <summary>The umbrella grant — full access to the whole Inventory System. Set from the Website
    /// Admin's Roles editor ("Inventory System" toggle).</summary>
    public const string Umbrella = "InventorySystem";

    /// <summary>The grantable tabs, in sidebar order. Overview is intentionally NOT here — it is the
    /// landing page and is shown to anyone who can enter the area at all.</summary>
    public static readonly List<InvSection> All = new()
    {
        new("Inv.Pos",     "Point of Sale",  "Till sessions, registers & POS settings"),
        new("Inv.Sales",   "Sales",          "Completed, outstanding & saved carts"),
        new("Inv.Crm",     "CRM",            "Customers & discounts"),
        new("Inv.Stock",   "Inventory",      "Products, stock, adjustments, stock-take, transfers & returns"),
        new("Inv.Reports", "Reports",        "Stock & sales reports"),
        new("Inv.Admin",   "Administration", "Staff, branches & activity log"),
    };

    /// <summary>Each Inventory-area controller belongs to exactly one tab. Controllers not listed
    /// (e.g. Overview) have no tab gate — reachable by anyone who can enter the area.</summary>
    private static readonly Dictionary<string, string> ControllerTab = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Till"]        = "Inv.Pos",
        ["Sales"]       = "Inv.Sales",
        ["Crm"]         = "Inv.Crm",
        ["Products"]    = "Inv.Stock",
        ["Categories"]  = "Inv.Stock",
        ["Stock"]       = "Inv.Stock",
        ["Adjustments"] = "Inv.Stock",
        ["Stocktake"]   = "Inv.Stock",
        ["Transfers"]   = "Inv.Stock",
        ["Returns"]     = "Inv.Stock",
        ["Reprint"]     = "Inv.Stock",
        ["Reports"]     = "Inv.Reports",
        ["Org"]         = "Inv.Admin",
    };

    /// <summary>The tab a controller belongs to, or <c>null</c> for controllers with no tab gate
    /// (Overview) — those are open to anyone who can enter the Inventory System.</summary>
    public static string? TabForController(string? controller) =>
        !string.IsNullOrEmpty(controller) && ControllerTab.TryGetValue(controller, out var tab) ? tab : null;

    /// <summary>True if the permission key is one of the Inventory System's per-tab keys ("Inv.*").
    /// Used to scope the Inventory Roles editor so it never touches Website Admin grants (and vice
    /// versa). The umbrella key ("InventorySystem") is owned by the Admin editor, so it is deliberately
    /// NOT matched here.</summary>
    public static bool IsPerTabKey(string key) =>
        !string.IsNullOrEmpty(key) && key.StartsWith("Inv.", StringComparison.Ordinal);

    /// <summary>Validates an Inventory permission key (umbrella or a per-tab base key) so
    /// <c>PermissionService</c> can persist it. Manage variants ("&lt;key&gt;:manage") are validated by
    /// the Admin permission parser against the base key returned here.</summary>
    public static bool IsValidKey(string key) =>
        key == Umbrella || All.Any(s => s.Key == key);
}
