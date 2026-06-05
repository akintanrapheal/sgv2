using System.Text.Json.Serialization;

namespace SterlingLams.Web.Services.ERPNext.ERPNextModels;

/// <summary>Represents an Item record from the ERPNext Item doctype.</summary>
public class ERPNextItem
{
    /// <summary>The item code (primary key in ERPNext — same as the item name field).</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("item_name")]
    public string ItemName { get; set; } = string.Empty;

    [JsonPropertyName("standard_rate")]
    public decimal StandardRate { get; set; }

    [JsonPropertyName("item_group")]
    public string? ItemGroup { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("disabled")]
    public int Disabled { get; set; }

    public string ItemCode => Name;
    public bool IsActive => Disabled == 0;
}

/// <summary>Represents a Bin record from ERPNext (stock per item per warehouse).</summary>
public class ERPNextBin
{
    [JsonPropertyName("item_code")]
    public string ItemCode { get; set; } = string.Empty;

    [JsonPropertyName("warehouse")]
    public string Warehouse { get; set; } = string.Empty;

    [JsonPropertyName("actual_qty")]
    public decimal ActualQty { get; set; }

    [JsonPropertyName("reserved_qty")]
    public decimal ReservedQty { get; set; }

    public int AvailableQty => (int)Math.Max(0, ActualQty - ReservedQty);
}

/// <summary>Generic ERPNext list API response envelope.</summary>
public class ERPNextListResponse<T>
{
    [JsonPropertyName("data")]
    public List<T> Data { get; set; } = new();
}

/// <summary>Generic ERPNext single-document API response envelope.</summary>
public class ERPNextSingleResponse<T>
{
    [JsonPropertyName("data")]
    public T Data { get; set; } = default!;
}

/// <summary>Minimal document type that has a name (used for create responses).</summary>
public class ERPNextNamedDocument
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}
