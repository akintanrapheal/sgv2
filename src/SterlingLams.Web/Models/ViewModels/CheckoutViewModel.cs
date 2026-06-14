using System.ComponentModel.DataAnnotations;

namespace SterlingLams.Web.Models.ViewModels;

public class CheckoutViewModel : IValidatableObject
{
    public CartViewModel Cart { get; set; } = new();

    public FulfillmentChoice FulfillmentType { get; set; } = FulfillmentChoice.Delivery;

    // Store Pickup
    public int? SelectedStoreId { get; set; }
    public List<StorePickupOptionViewModel> AvailableStores { get; set; } = new();

    // Delivery
    public DeliveryAddressViewModel DeliveryAddress { get; set; } = new();

    // Delivery zone & type selection
    public string SelectedDeliveryType { get; set; } = "Standard";
    public List<DeliveryOptionViewModel> DeliveryOptions { get; set; } = new();

    // Payment
    public string PaymentProvider { get; set; } = "Paystack";
    public string? PaystackPublicKey { get; set; }

    // All Nigerian states + Lagos LGAs for dropdowns (passed from controller)
    public string[] NigerianStates { get; set; } = Array.Empty<string>();
    public string[] LagosLGAs { get; set; } = Array.Empty<string>();

    // Pricing data serialized to JSON for client-side zone detection
    public string DeliveryPricingJson { get; set; } = "{}";

    // Totals
    public decimal Subtotal { get; set; }
    public decimal DeliveryFee { get; set; }
    public decimal DiscountAmount { get; set; }
    public string? AppliedDiscountCode { get; set; }
    public string? DiscountDescription { get; set; }
    public decimal Total => Subtotal - DiscountAmount + DeliveryFee;
    public string FormattedTotal => $"₦{Total:N0}";
    public string FormattedSubtotal => $"₦{Subtotal:N0}";
    public string FormattedDeliveryFee => DeliveryFee == 0 ? "Free" : $"₦{DeliveryFee:N0}";
    public string FormattedDiscount => $"-₦{DiscountAmount:N0}";
    public bool HasDiscount => DiscountAmount > 0;

    // Guest checkout fields (used when user is not signed in)
    [EmailAddress]
    public string? GuestEmail { get; set; }
    public string? GuestName { get; set; }
    public string? GuestPhone { get; set; }

    // Conditional validation: a delivery address is only required for Delivery; a store is only
    // required for Store Pickup. (Previously the address fields were unconditionally [Required],
    // which blocked pickup checkouts.)
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (FulfillmentType == FulfillmentChoice.Delivery)
        {
            if (string.IsNullOrWhiteSpace(DeliveryAddress.FullName))
                yield return new ValidationResult("Please enter the recipient's full name.", new[] { "DeliveryAddress.FullName" });
            if (string.IsNullOrWhiteSpace(DeliveryAddress.Phone))
                yield return new ValidationResult("Please enter a phone number.", new[] { "DeliveryAddress.Phone" });
            if (string.IsNullOrWhiteSpace(DeliveryAddress.Line1))
                yield return new ValidationResult("Please enter the delivery address.", new[] { "DeliveryAddress.Line1" });
            if (string.IsNullOrWhiteSpace(DeliveryAddress.City))
                yield return new ValidationResult("Please enter the city.", new[] { "DeliveryAddress.City" });
            if (string.IsNullOrWhiteSpace(DeliveryAddress.State))
                yield return new ValidationResult("Please select the state.", new[] { "DeliveryAddress.State" });
        }
        else // Store Pickup
        {
            if (SelectedStoreId == null)
                yield return new ValidationResult("Please select a store for pickup.", new[] { "SelectedStoreId" });
        }
    }
}

public class DeliveryOptionViewModel
{
    public string Type { get; set; } = "Standard";
    public string Label { get; set; } = string.Empty;
    public decimal Fee { get; set; }
    public string Timeframe { get; set; } = string.Empty;
    public string FormattedFee => $"₦{Fee:N0}";
}

public enum FulfillmentChoice
{
    Delivery,
    StorePickup
}

public class StorePickupOptionViewModel
{
    public int StoreId { get; set; }
    public string StoreName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string? OpeningHours { get; set; }
    public bool AllItemsAvailable { get; set; }
}

public class DeliveryAddressViewModel
{
    // Not unconditionally [Required] — CheckoutViewModel.Validate enforces these only for Delivery.
    public string FullName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Line1 { get; set; } = string.Empty;
    public string? Line2 { get; set; }
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Country { get; set; } = "Nigeria";
    public string? PostalCode { get; set; }
    public bool SaveAddress { get; set; }
}
