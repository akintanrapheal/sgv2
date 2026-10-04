using System.Text.Json.Serialization;

namespace SterlingLams.Web.Models.ViewModels;

public class CartItemViewModel
{
    public int ProductId { get; set; }
    public int? VariantId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string? VariantName { get; set; }
    public string ImageUrl { get; set; } = "/images/placeholder.jpg";
    public string Slug { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
    // Computed fields are recomputed from the stored values above, so they're kept out of the
    // persisted cart cookie (keeps it small — see CartStore).
    [JsonIgnore] public decimal LineTotal => UnitPrice * Quantity;
    [JsonIgnore] public string FormattedLineTotal => $"₦{LineTotal:N0}";
    [JsonIgnore] public string FormattedUnitPrice => $"₦{UnitPrice:N0}";
    public int MaxQuantity { get; set; } = 10;
}

public class CartViewModel
{
    public List<CartItemViewModel> Items { get; set; } = new();
    /// <summary>Items the shopper moved out of the bag to "save for later" — kept in the session
    /// cart but not counted toward totals or checkout.</summary>
    public List<CartItemViewModel> SavedItems { get; set; } = new();
    [JsonIgnore] public decimal Subtotal => Items.Sum(i => i.LineTotal);
    [JsonIgnore] public string FormattedSubtotal => $"₦{Subtotal:N0}";
    [JsonIgnore] public int TotalItems => Items.Sum(i => i.Quantity);
    [JsonIgnore] public bool IsEmpty => !Items.Any();

    // Discount
    public string? AppliedDiscountCode { get; set; }
    public string? DiscountDescription { get; set; }
    public decimal DiscountAmount { get; set; }
    public bool FreeShipping { get; set; }
    /// <summary>The applied free-shipping discount only waives the fee for Lagos/Abuja deliveries.</summary>
    public bool FreeShippingLagosAbujaOnly { get; set; }
    public bool IsAutomaticDiscount { get; set; }
    [JsonIgnore] public bool HasDiscount => DiscountAmount > 0 || FreeShipping;
    [JsonIgnore] public string FormattedDiscount => $"-₦{DiscountAmount:N0}";

    [JsonIgnore] public decimal Total => Subtotal - DiscountAmount;
    [JsonIgnore] public string FormattedTotal => $"₦{Total:N0}";
}
