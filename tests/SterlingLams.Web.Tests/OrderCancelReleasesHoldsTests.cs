using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using SterlingLams.Web.Services.Payment;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>Cancelling an order must free the stock still HELD for it, so cancelled online orders don't
/// leave units reserved ("held for online orders") forever. Regression for SGW-30199.</summary>
public class OrderCancelReleasesHoldsTests
{
    private static OrderFulfilmentService Fulfilment(TestDb t) =>
        new(t.Db, new StockService(t.Db), NullLogger<OrderFulfilmentService>.Instance,
            new FakePayment(), new FakeEmail(), new FakeSettings());

    private static OrderStatusService StatusSvc(TestDb t, OrderFulfilmentService fulfilment)
    {
        var transfers = new TransferWorkflowService(t.Db, new StockService(t.Db), fulfilment);
        return new OrderStatusService(t.Db, fulfilment, transfers, new FakeLogistics(),
            new FakeEmail(), new FakeSettings(), new FakeWhatsApp(), new FakeAudit(),
            new HttpContextAccessor(), links: null!);
    }

    [Fact]
    public async Task Cancelling_an_awaiting_transfer_order_releases_every_hold()
    {
        using var t = new TestDb();
        var user = t.SeedUser();
        var (abuja, allen, ikota) = t.SeedBranches();
        var p = t.SeedProduct(price: 1000m);
        t.SetStock(p.Id, abuja.Id, 1);
        t.SetStock(p.Id, allen.Id, 1);
        t.SetStock(p.Id, ikota.Id, 1);

        // Lagos order for all 3 units → fulfilled from Allen, cross-branch → AwaitingTransfer with holds.
        var order = t.NewDeliveryOrder(user, "Lagos", "Ikeja", (p, 3));
        var fulfilment = Fulfilment(t);
        await fulfilment.FulfilPaidOrderAsync(order.Id);

        // Sanity: everything is reserved and two transfers are pending.
        Assert.Equal(OrderStatus.AwaitingTransfer, (await t.Db.Orders.FindAsync(order.Id))!.Status);
        Assert.Equal(1, t.Inv(p.Id, abuja.Id).QuantityReserved);
        Assert.Equal(1, t.Inv(p.Id, allen.Id).QuantityReserved);
        Assert.Equal(1, t.Inv(p.Id, ikota.Id).QuantityReserved);
        // The fulfilling branch (Allen) holds its 1 local unit as a reservation row; the other two
        // units are reserved at source via the two pending transfers.
        Assert.Equal(1, await t.Db.StockReservations.CountAsync(r => r.OrderId == order.Id));
        Assert.Equal(2, await t.Db.StockTransfers.CountAsync(x => x.OrderId == order.Id));

        // Cancel it.
        var tracked = await t.Db.Orders.FirstAsync(o => o.Id == order.Id);
        await StatusSvc(t, fulfilment).ApplyAsync(tracked, OrderStatus.Cancelled, "Tester");

        // Status is Cancelled, and NOTHING is held any more.
        Assert.Equal(OrderStatus.Cancelled, (await t.Db.Orders.FindAsync(order.Id))!.Status);
        Assert.Equal(0, t.Inv(p.Id, abuja.Id).QuantityReserved);
        Assert.Equal(0, t.Inv(p.Id, allen.Id).QuantityReserved);
        Assert.Equal(0, t.Inv(p.Id, ikota.Id).QuantityReserved);
        Assert.Equal(0, await t.Db.StockReservations.CountAsync(r => r.OrderId == order.Id));
        Assert.All(await t.Db.StockTransfers.Where(x => x.OrderId == order.Id).ToListAsync(),
            x => Assert.Equal(TransferStatus.Cancelled, x.Status));

        // On-hand is untouched — releasing a HOLD is not a sale reversal (no double-restock).
        Assert.Equal(1, t.Inv(p.Id, abuja.Id).QuantityOnHand);
        Assert.Equal(1, t.Inv(p.Id, allen.Id).QuantityOnHand);
        Assert.Equal(1, t.Inv(p.Id, ikota.Id).QuantityOnHand);
    }

    [Fact]
    public async Task Cancelling_a_single_branch_order_touches_no_stock()
    {
        using var t = new TestDb();
        var user = t.SeedUser();
        var (abuja, allen, ikota) = t.SeedBranches();
        var p = t.SeedProduct();
        t.SetStock(p.Id, allen.Id, 5);   // nearest branch has it all → ships now, no reservation

        var order = t.NewDeliveryOrder(user, "Lagos", "Ikeja", (p, 2));
        var fulfilment = Fulfilment(t);
        await fulfilment.FulfilPaidOrderAsync(order.Id);

        Assert.Equal(OrderStatus.Confirmed, (await t.Db.Orders.FindAsync(order.Id))!.Status);
        Assert.Equal(3, t.Inv(p.Id, allen.Id).QuantityOnHand);   // 2 sold
        Assert.Equal(0, await t.Db.StockReservations.CountAsync(r => r.OrderId == order.Id));

        var tracked = await t.Db.Orders.FirstAsync(o => o.Id == order.Id);
        await StatusSvc(t, fulfilment).ApplyAsync(tracked, OrderStatus.Cancelled, "Tester");

        // Cancel is a clean no-op for holds (there were none); on-hand stays where the sale left it —
        // returning sold units is the refund restock/write-off decision, not cancellation.
        Assert.Equal(OrderStatus.Cancelled, (await t.Db.Orders.FindAsync(order.Id))!.Status);
        Assert.Equal(0, t.Inv(p.Id, allen.Id).QuantityReserved);
        Assert.Equal(3, t.Inv(p.Id, allen.Id).QuantityOnHand);
    }

    // ── minimal fakes ───────────────────────────────────────────────────────────
    private sealed class FakeEmail : IEmailService
    {
        public Task<bool> SendAsync(string toEmail, string subject, string innerHtml, string? toName = null, CancellationToken ct = default, string? fromOverride = null) => Task.FromResult(true);
        public Task<string> RenderAsync(string subject, string innerHtml, int? logoHeight = null) => Task.FromResult(innerHtml);
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

    private sealed class FakePayment : IPaymentService
    {
        public string ProviderName => "Test";
        public Task<InitiatePaymentResult> InitiatePaymentAsync(InitiatePaymentRequest request) => throw new System.NotImplementedException();
        public Task<VerifyPaymentResult> VerifyPaymentAsync(string reference) => throw new System.NotImplementedException();
        public Task<bool> ValidateWebhookAsync(string payload, string signature) => Task.FromResult(false);
        public Task<RefundResult> RefundPaymentAsync(RefundPaymentRequest request) => Task.FromResult(new RefundResult { Success = true, Supported = true });
    }

    private sealed class FakeLogistics : SterlingLams.Web.Services.Logistics.ILogisticsDispatchService
    {
        public Task PushOrderAsync(int orderId) => Task.CompletedTask;
        public string ComputeSignature(string body) => "";
        public bool IsConfigured => false;
    }

    private sealed class FakeWhatsApp : IWhatsAppService
    {
        public Task<(bool Ok, string Message)> SendAsync(string toPhone, string body, CancellationToken ct = default) => Task.FromResult((true, ""));
        public Task<(bool Ok, string Message)> SendTemplateAsync(string toPhone, string contentSid, IDictionary<string, string> variables, CancellationToken ct = default) => Task.FromResult((true, ""));
        public Task NotifyOrderAsync(int orderId, WhatsAppOrderEvent evt, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> IsConfiguredAsync() => Task.FromResult(false);
    }

    private sealed class FakeAudit : IAuditService
    {
        public Task LogAsync(string action, string entityType, string? entityId, string description, string? changes = null, string? performedBy = null) => Task.CompletedTask;
    }
}
