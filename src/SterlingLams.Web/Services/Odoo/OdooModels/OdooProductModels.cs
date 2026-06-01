using System.Text.Json.Serialization;

namespace SterlingLams.Web.Services.Odoo.OdooModels;

public class OdooProduct
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("list_price")]
    public decimal ListPrice { get; set; }

    [JsonPropertyName("default_code")]
    public object? DefaultCode { get; set; }

    [JsonPropertyName("barcode")]
    public object? Barcode { get; set; }

    [JsonPropertyName("categ_id")]
    public object[] CategoryId { get; set; } = Array.Empty<object>();

    [JsonPropertyName("description_sale")]
    public object? DescriptionSale { get; set; }

    [JsonPropertyName("active")]
    public bool Active { get; set; }

    [JsonPropertyName("image_1920")]
    public object? Image { get; set; }

    public string? Sku => DefaultCode is string s && s != "false" ? s : null;
    public string? CategoryName => CategoryId.Length > 1 ? CategoryId[1]?.ToString() : null;
}

public class OdooStockQuant
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("product_id")]
    public object[] ProductId { get; set; } = Array.Empty<object>();

    [JsonPropertyName("location_id")]
    public object[] LocationId { get; set; } = Array.Empty<object>();

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; set; }

    [JsonPropertyName("reserved_quantity")]
    public decimal ReservedQuantity { get; set; }

    public int ProductOdooId => ProductId.Length > 0 ? Convert.ToInt32(ProductId[0]) : 0;
    public int LocationOdooId => LocationId.Length > 0 ? Convert.ToInt32(LocationId[0]) : 0;
    public string LocationName => LocationId.Length > 1 ? LocationId[1]?.ToString() ?? "" : "";
}

public class OdooSaleOrder
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("amount_total")]
    public decimal AmountTotal { get; set; }

    [JsonPropertyName("partner_id")]
    public object[] PartnerId { get; set; } = Array.Empty<object>();
}
