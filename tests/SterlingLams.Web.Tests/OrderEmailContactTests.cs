using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>A shared account (e.g. a customer-care shell email used for many buyers) must never make an
/// order show the wrong person: the per-order ContactName/ContactPhone wins over the account.</summary>
public class OrderEmailContactTests
{
    [Fact]
    public void Address_blocks_prefer_the_per_order_contact_over_the_shared_account()
    {
        var order = new Order
        {
            ContactName = "Real Buyer",
            ContactPhone = "08099999999",
            FulfillmentType = FulfillmentType.StorePickup,
            PickupStore = new Store { Name = "Sterlin Glams Ikota", Address = "Road 5, Shop J22, Ikota Ajah, Lagos" },
            User = new ApplicationUser { FirstName = "Shared", LastName = "Careemail", PhoneNumber = "08000000000" },
        };

        var (billing, shipping, pickupLabel) = OrderEmailTemplate.AddressBlocksFor(order, "Shared Careemail", "care@example.com");

        Assert.Contains("Real Buyer", billing);               // the real buyer, not the account name
        Assert.Contains("Tel: 08099999999", billing);          // the order's phone, not the account phone
        Assert.DoesNotContain("Shared Careemail", billing);
        Assert.DoesNotContain("Tel: 08000000000", billing);
        // Pickup shipping block carries the full branch address (delivery excluded).
        Assert.Contains("Real Buyer", shipping);
        Assert.Contains("Road 5, Shop J22, Ikota Ajah, Lagos", shipping);
        Assert.Contains("Ikota", pickupLabel);
    }

    [Fact]
    public void Address_blocks_fall_back_to_the_account_when_no_per_order_contact()
    {
        var order = new Order
        {
            FulfillmentType = FulfillmentType.StorePickup,
            PickupStore = new Store { Name = "Sterlin Glams Allen", Address = "Allen Avenue, Ikeja" },
            User = new ApplicationUser { FirstName = "Jane", LastName = "Doe", PhoneNumber = "08011111111" },
        };

        var (billing, _, _) = OrderEmailTemplate.AddressBlocksFor(order, "Jane Doe", "jane@example.com");

        Assert.Contains("Jane Doe", billing);
        Assert.Contains("Tel: 08011111111", billing);
    }
}
