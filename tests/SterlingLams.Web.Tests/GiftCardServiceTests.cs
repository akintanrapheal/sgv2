using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using Xunit;

namespace SterlingLams.Web.Tests;

public class GiftCardServiceTests
{
    private static GiftCardService NewService(TestDb t)
        => new(t.Db, new StubSettings(), NullLogger<GiftCardService>.Instance);

    private static async Task<Order> SeedOrderAsync(TestDb t, string code, decimal giftAmount)
    {
        var user = t.SeedUser();
        var order = new Order
        {
            OrderNumber = "SL-30001",
            UserId = user.Id,
            GiftCardCode = code,
            GiftCardAmount = giftAmount,
            Total = 1m,
        };
        t.Db.Orders.Add(order);
        await t.Db.SaveChangesAsync();
        return order;
    }

    [Fact]
    public async Task IssueAsync_creates_card_with_balance_and_ledger()
    {
        using var t = new TestDb();
        var svc = NewService(t);

        var card = await svc.IssueAsync(10000m, "Ada", "ada@test.local", "Birthday", null, null);

        Assert.StartsWith("SLGC-", card.Code);
        Assert.Equal(10000m, card.InitialAmount);
        Assert.Equal(10000m, card.Balance);
        Assert.True(card.IsActive);
        var txn = Assert.Single(t.Db.GiftCardTransactions.Where(x => x.GiftCardId == card.Id));
        Assert.Equal(GiftCardTxnType.Issue, txn.Type);
        Assert.Equal(10000m, txn.Amount);
    }

    [Fact]
    public async Task ValidateAsync_reports_ok_for_active_card_and_fails_otherwise()
    {
        using var t = new TestDb();
        var svc = NewService(t);
        var card = await svc.IssueAsync(5000m, null, null, null, null, null);

        var ok = await svc.ValidateAsync(card.Code);
        Assert.True(ok.Ok);
        Assert.Equal(5000m, ok.Balance);

        var unknown = await svc.ValidateAsync("SLGC-NOPE-NOPE");
        Assert.False(unknown.Ok);

        var blank = await svc.ValidateAsync("   ");
        Assert.False(blank.Ok);
    }

    [Fact]
    public async Task RedeemForOrderAsync_draws_balance_once_and_is_idempotent()
    {
        using var t = new TestDb();
        var svc = NewService(t);
        var card = await svc.IssueAsync(10000m, null, null, null, null, null);
        var order = await SeedOrderAsync(t, card.Code, 1499m);

        await svc.RedeemForOrderAsync(order.Id);
        var afterFirst = await t.Db.GiftCards.AsNoTracking().FirstAsync(g => g.Id == card.Id);
        Assert.Equal(8501m, afterFirst.Balance);
        Assert.NotNull((await t.Db.Orders.AsNoTracking().FirstAsync(o => o.Id == order.Id)).GiftCardRedeemedAt);
        Assert.Single(t.Db.GiftCardTransactions.Where(x => x.GiftCardId == card.Id && x.Type == GiftCardTxnType.Redeem));

        // Second call (e.g. webhook after the callback) must not draw again.
        await svc.RedeemForOrderAsync(order.Id);
        var afterSecond = await t.Db.GiftCards.AsNoTracking().FirstAsync(g => g.Id == card.Id);
        Assert.Equal(8501m, afterSecond.Balance);
        Assert.Single(t.Db.GiftCardTransactions.Where(x => x.GiftCardId == card.Id && x.Type == GiftCardTxnType.Redeem));
    }

    [Fact]
    public async Task RedeemForOrderAsync_clamps_to_available_balance()
    {
        using var t = new TestDb();
        var svc = NewService(t);
        var card = await svc.IssueAsync(500m, null, null, null, null, null);
        var order = await SeedOrderAsync(t, card.Code, 1000m); // earmarked more than the card holds

        await svc.RedeemForOrderAsync(order.Id);
        var after = await t.Db.GiftCards.AsNoTracking().FirstAsync(g => g.Id == card.Id);
        Assert.Equal(0m, after.Balance); // never goes negative
    }

    [Fact]
    public async Task ReverseForOrderAsync_returns_drawn_balance_once()
    {
        using var t = new TestDb();
        var svc = NewService(t);
        var card = await svc.IssueAsync(10000m, null, null, null, null, null);
        var order = await SeedOrderAsync(t, card.Code, 1499m);
        await svc.RedeemForOrderAsync(order.Id);

        await svc.ReverseForOrderAsync(order.Id);
        var afterFirst = await t.Db.GiftCards.AsNoTracking().FirstAsync(g => g.Id == card.Id);
        Assert.Equal(10000m, afterFirst.Balance);

        // Idempotent — a second reversal does not credit again.
        await svc.ReverseForOrderAsync(order.Id);
        var afterSecond = await t.Db.GiftCards.AsNoTracking().FirstAsync(g => g.Id == card.Id);
        Assert.Equal(10000m, afterSecond.Balance);
    }
}
