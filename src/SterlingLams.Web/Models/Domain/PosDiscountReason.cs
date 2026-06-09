namespace SterlingLams.Web.Models.Domain;

public class PosDiscountReason
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public List<PosDiscountPreset> Presets { get; set; } = new();
}

public class PosDiscountPreset
{
    public int Id { get; set; }
    public int ReasonId { get; set; }
    public PosDiscountReason Reason { get; set; } = null!;
    public string Label { get; set; } = string.Empty;
    public string Type { get; set; } = "Percentage"; // Percentage | Amount | NewPrice
    public decimal Value { get; set; }
    public int SortOrder { get; set; }
}
