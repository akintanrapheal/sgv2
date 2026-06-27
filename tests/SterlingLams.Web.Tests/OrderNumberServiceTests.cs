using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>
/// OrderNumberService on the SQLite test harness uses the count-based fallback (the Postgres
/// sequence path only runs on Npgsql). Confirms the short number + channel prefix.
/// </summary>
public class OrderNumberServiceTests
{
    [Fact]
    public async Task First_online_number_starts_at_30001_with_default_prefix()
    {
        using var t = new TestDb();
        var svc = new OrderNumberService(t.Db, new StubSettings());

        var n = await svc.NextAsync(OrderChannel.Online);

        Assert.Equal("SL-30001", n);
    }

    [Fact]
    public async Task Pos_channel_uses_pos_prefix()
    {
        using var t = new TestDb();
        var svc = new OrderNumberService(t.Db, new StubSettings());

        var n = await svc.NextAsync(OrderChannel.Pos);

        Assert.Equal("POS-30001", n);
    }

    [Fact]
    public async Task Number_increments_with_existing_order_count()
    {
        using var t = new TestDb();
        var user = t.SeedUser();
        t.Db.Orders.Add(new Order { OrderNumber = "SL-30001", UserId = user.Id, Total = 1m });
        await t.Db.SaveChangesAsync();

        var svc = new OrderNumberService(t.Db, new StubSettings());
        var n = await svc.NextAsync(OrderChannel.Online);

        Assert.Equal("SL-30002", n);
    }

    [Fact]
    public async Task Custom_prefix_from_settings_is_used()
    {
        using var t = new TestDb();
        var settings = new StubSettings(new() { ["order.number_prefix"] = "ORD-" });
        var svc = new OrderNumberService(t.Db, settings);

        var n = await svc.NextAsync(OrderChannel.Online);

        Assert.Equal("ORD-30001", n);
    }
}
