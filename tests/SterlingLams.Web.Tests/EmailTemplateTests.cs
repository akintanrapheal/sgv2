using System.Collections.Generic;
using System.Threading.Tasks;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>Order emails show each product's SKU, and the shipping line is the Glams-branded delivery
/// method (e.g. "₦4,000.00 via Glams Priority Delivery (within 48 hrs)").</summary>
public class EmailTemplateTests
{
    private static OrderEmailTemplate.Item Item(string name, string? sku) =>
        new(name, "Rose Gold", 1, 16000m, null, sku);

    [Fact]
    public void Build_renders_the_product_sku()
    {
        var html = OrderEmailTemplate.Build(
            heading: "Order", introHtml: "", orderNumber: "SGW-1", orderDate: System.DateTime.UtcNow,
            items: new[] { Item("Pearl Ball Necklace", "5012176") }, subtotal: 16000m,
            shippingLabel: "X", total: 20000m, paymentMethod: "Card",
            billingLines: new List<string>(), shippingLines: new List<string>());

        Assert.Contains("#5012176", html);
    }

    [Fact]
    public void StatusUpdate_renders_the_product_sku()
    {
        var html = OrderEmailTemplate.BuildStatusUpdate(
            heading: "Shipped", introHtml: "", orderNumber: "SGW-1",
            items: new[] { Item("Pearl Ball Necklace", "5012176") }, total: 20000m);

        Assert.Contains("#5012176", html);
    }

    [Fact]
    public async Task Shipping_label_is_the_glams_branded_method_with_fee()
    {
        var svc = new DeliveryZoneService(new FakeSettings());

        var express = new Order
        {
            FulfillmentType = FulfillmentType.Delivery, DeliveryType = "Express", DeliveryFee = 4000m,
            DeliveryAddress = new Address { State = "Lagos", City = "Ikeja" }
        };
        var label = await svc.EmailShippingLabelAsync(express, null);
        Assert.Contains("₦4,000.00 via", label);
        Assert.Contains("Glams Priority Delivery", label);

        var sameDay = new Order
        {
            FulfillmentType = FulfillmentType.Delivery, DeliveryType = "SameDay", DeliveryFee = 5000m,
            DeliveryAddress = new Address { State = "Lagos", City = "Ajah" }
        };
        Assert.Contains("Glams Same-Day Delivery", await svc.EmailShippingLabelAsync(sameDay, null));

        var pickup = new Order { FulfillmentType = FulfillmentType.StorePickup };
        Assert.Equal("Store pickup — Ikota, Ajah", await svc.EmailShippingLabelAsync(pickup, "Store pickup — Ikota, Ajah"));
    }

    private sealed class FakeSettings : ISettingsService
    {
        public Task<string> GetAsync(string key, string defaultValue = "") => Task.FromResult(defaultValue);
        public Task<bool> GetBoolAsync(string key, bool defaultValue = false) => Task.FromResult(defaultValue);
        public Task<decimal> GetDecimalAsync(string key, decimal defaultValue = 0) => Task.FromResult(defaultValue);
        public Task<int> GetIntAsync(string key, int defaultValue = 0) => Task.FromResult(defaultValue);
        public Task SaveManyAsync(Dictionary<string, string> values) => Task.CompletedTask;
        public Task<List<SiteSetting>> GetGroupAsync(string group) => Task.FromResult(new List<SiteSetting>());
        public Task<List<SiteSetting>> GetAllAsync() => Task.FromResult(new List<SiteSetting>());
        public void ClearCache() { }
    }
}
