using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>The notifier only pushes WEBSITE (online) orders to Retainful, only when it's enabled, keys
/// every event to the order number, and carries the cart line items + a recovery URL (CHECKOUT_CREATED
/// only). These guard the gating and the abandoned-cart payload Retainful needs.</summary>
public class RetainfulOrderNotifierTests
{
    private sealed class CapturingClient : IRetainfulClient
    {
        public readonly List<(string ev, string uid, string? email, decimal total, int items,
            IReadOnlyList<RetainfulLineItem>? lines, string? recoveryUrl)> Calls = new();
        public Task SubscribeNewsletterAsync(string email, string? firstName = null, string? lastName = null) => Task.CompletedTask;
        public Task SendOrderEventAsync(string eventName, string uniqueIdentifier, string? email, string? phone,
            string? firstName, string? lastName, decimal total, int itemCount, string? status = null,
            string? channel = null, IDictionary<string, string>? extra = null,
            IReadOnlyList<RetainfulLineItem>? lines = null, string? recoveryUrl = null, string currency = "NGN")
        {
            Calls.Add((eventName, uniqueIdentifier, email, total, itemCount, lines, recoveryUrl));
            return Task.CompletedTask;
        }
        public Task<(bool ok, string detail)> TestAsync() => Task.FromResult((true, ""));
    }

    private static RetainfulOrderNotifier Make(TestDb t, IRetainfulClient c, bool enabled, string? baseUrl = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = baseUrl })
            .Build();
        return new(t.Db, c, new StubSettings(new() { ["retainful.enabled"] = enabled ? "true" : "false" }),
            config, new EphemeralDataProtectionProvider(), NullLogger<RetainfulOrderNotifier>.Instance);
    }

    [Fact]
    public async Task Fires_each_event_for_an_online_order_keyed_to_the_order_number()
    {
        using var t = new TestDb();
        var u = t.SeedUser();
        var p = t.SeedProduct(5000m);
        var order = t.NewDeliveryOrder(u, "Lagos", "Ikeja", (p, 2));
        var client = new CapturingClient();

        await Make(t, client, enabled: true).NotifyAsync(order.Id, "CHECKOUT_CREATED", "ORDER_PLACED");

        Assert.Equal(2, client.Calls.Count);
        Assert.Equal("CHECKOUT_CREATED", client.Calls[0].ev);
        Assert.Equal("ORDER_PLACED", client.Calls[1].ev);
        Assert.Equal(order.OrderNumber, client.Calls[0].uid);
        Assert.Equal("buyer@test.local", client.Calls[0].email);
        Assert.Equal(10000m, client.Calls[0].total);
        Assert.Equal(2, client.Calls[0].items);
    }

    [Fact]
    public async Task Sends_line_items_and_recovery_url_only_on_checkout_created()
    {
        using var t = new TestDb();
        var u = t.SeedUser();
        var p = t.SeedProduct(5000m);
        var order = t.NewDeliveryOrder(u, "Lagos", "Ikeja", (p, 2));
        var client = new CapturingClient();

        await Make(t, client, enabled: true, baseUrl: "https://shop.test")
            .NotifyAsync(order.Id, "CHECKOUT_CREATED", "ORDER_PAID");

        // Line items ride on every event.
        Assert.NotNull(client.Calls[0].lines);
        Assert.Single(client.Calls[0].lines!);
        Assert.Equal(2, client.Calls[0].lines![0].Quantity);
        Assert.Equal(p.Id.ToString(), client.Calls[0].lines![0].ProductId);

        // recovery_url only on CHECKOUT_CREATED.
        Assert.NotNull(client.Calls[0].recoveryUrl);
        Assert.Contains("/checkout/recover?t=", client.Calls[0].recoveryUrl!);
        Assert.Null(client.Calls[1].recoveryUrl);
    }

    [Fact]
    public async Task No_recovery_url_without_a_base_url()
    {
        using var t = new TestDb();
        var u = t.SeedUser();
        var p = t.SeedProduct(5000m);
        var order = t.NewDeliveryOrder(u, "Lagos", "Ikeja", (p, 1));
        var client = new CapturingClient();

        await Make(t, client, enabled: true, baseUrl: null).NotifyAsync(order.Id, "CHECKOUT_CREATED");

        Assert.Null(client.Calls[0].recoveryUrl);
    }

    [Fact]
    public async Task Skips_everything_when_retainful_is_disabled()
    {
        using var t = new TestDb();
        var u = t.SeedUser();
        var p = t.SeedProduct();
        var order = t.NewDeliveryOrder(u, "Lagos", "Ikeja", (p, 1));
        var client = new CapturingClient();

        await Make(t, client, enabled: false).NotifyAsync(order.Id, "ORDER_PAID");

        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Skips_offline_pos_orders()
    {
        using var t = new TestDb();
        var u = t.SeedUser();
        var p = t.SeedProduct();
        var order = t.NewDeliveryOrder(u, "Lagos", "Ikeja", (p, 1));
        order.Channel = OrderChannel.Pos;
        await t.Db.SaveChangesAsync();
        var client = new CapturingClient();

        await Make(t, client, enabled: true).NotifyAsync(order.Id, "ORDER_PAID");

        Assert.Empty(client.Calls);
    }
}
