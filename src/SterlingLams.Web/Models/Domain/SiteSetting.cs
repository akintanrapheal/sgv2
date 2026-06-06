namespace SterlingLams.Web.Models.Domain;

public class SiteSetting
{
    public int Id { get; set; }

    /// <summary>Unique dot-separated key e.g. "shipping.free_threshold"</summary>
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    /// <summary>Tab group displayed in admin e.g. "Shipping"</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>Human-readable label shown in the form</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Input type: text | number | boolean | textarea | email | url | tel</summary>
    public string Type { get; set; } = "text";

    public string? Description { get; set; }
    public int SortOrder { get; set; }
}
