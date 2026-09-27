namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// A magazine-style lookbook campaign: one full-width photo with clickable "shop the look" hotspots.
/// Stored (not an EF entity) as the <c>lookbook.campaigns</c> JSON setting — no schema/migration.
/// </summary>
public class LookbookCampaign
{
    public string Image { get; set; } = "";
    public string? Heading { get; set; }
    public string? Subtext { get; set; }
    public List<LookbookHotspot> Hotspots { get; set; } = new();
}

/// <summary>A dot placed on the campaign photo, positioned as a percentage of the image, linked to a product.</summary>
public class LookbookHotspot
{
    /// <summary>Horizontal position, 0–100 (% of image width).</summary>
    public double X { get; set; }
    /// <summary>Vertical position, 0–100 (% of image height).</summary>
    public double Y { get; set; }
    public int ProductId { get; set; }
}
