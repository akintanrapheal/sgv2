using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>The notifier only pushes WEBSITE (online) orders to Retainful, only when it's enabled, and
/// keys every event to the order number. These guard the gating that keeps POS sales out and avoids
/// firing when the integration is off.</summary>
public class RetainfulOrderNotifierTests
{
    private sealed class CapturingClient : IRetainfulClient
    {
        public readonly List<(string ev, string uid, string? email, decimal total, int items)> Calls = new();
        public Task SubscribeNewsletterAsync(string email, string? firstName = null, string? lastName = null) => Task.CompletedTask;
        public Task SendOrderEventAsync(string eventName, string uniqueIdentifier, string? email, string? phone,
            string? firstName, string? lastName, decimal total, int itemCount, string? status = null,
            string? channel = null, IDictionary<string, string>? extra = null)
        {
            Calls.Add((eventName, uniqueIdentifier, email, total, itemCount));
            return Task.CompletedTask;
        }
        public Task<(bool ok, string detail)> TestAsync() => Task.FromResult((true, ""));
    }

    private static RetainfulOrderNotifier Make(TestDb t, IRetainfulClient c, bool enabled) =>
        new(t.Db, c, new StubSettings(new() { ["retainful.enabled"] = enabled ? "true" : "false" }),
            NullLogger<RetainfulOrderNotifier>.Instance);

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
