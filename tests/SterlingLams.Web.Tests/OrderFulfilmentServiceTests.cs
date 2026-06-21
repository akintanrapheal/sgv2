using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using SterlingLams.Web.Services.Payment;
using Xunit;

namespace SterlingLams.Web.Tests;

public class OrderFulfilmentServiceTests
{
    private static OrderFulfilmentService Svc(TestDb t) =>
        new(t.Db, new StockService(t.Db), NullLogger<OrderFulfilmentService>.Instance,
            new FakePayment(), new FakeEmail(), new FakeSettings());

    [Fact]
    public async Task Fulfil_consolidates_via_transfers_then_sells_from_nearest_branch()
    {
        using var t = new TestDb();
        var user = t.SeedUser();
        var (abuja, allen, ikota) = t.SeedBranches();
        var p = t.SeedProduct(price: 1000m);
        // one unit in each branch
        t.SetStock(p.Id, abuja.Id, 1);
        t.SetStock(p.Id, allen.Id, 1);
        t.SetStock(p.Id, ikota.Id, 1);

        // customer in Lagos/Ikeja buys all 3
        var order = t.NewDeliveryOrder(user, "Lagos", "Ikeja", (p, 3));

        await Svc(t).FulfilPaidOrderAsync(order.Id);

        // Fulfilled from Allen (nearest). Cross-branch: stock is HELD (reserved), not yet moved/sold —
        // on-hand stays put at every branch until the transfers are dispatched & received.
        var fulfilled = await t.Db.Orders.FindAsync(order.Id);
        Assert.Equal(allen.Id, fulfilled!.FulfillingStoreId);
        Assert.Equal(OrderStatus.AwaitingTransfer, fulfilled.Status);
        Assert.Equal(1, t.Inv(p.Id, abuja.Id).QuantityOnHand); // reserved at source until dispatch
        Assert.Equal(1, t.Inv(p.Id, abuja.Id).QuantityReserved);
        Assert.Equal(1, t.Inv(p.Id, allen.Id).QuantityOnHand);  // local unit held (reserved), not yet sold
        Assert.Equal(1, t.Inv(p.Id, allen.Id).QuantityReserved);
        Assert.Equal(1, t.Inv(p.Id, ikota.Id).QuantityOnHand); // reserved at source until dispatch
        Assert.Equal(1, t.Inv(p.Id, ikota.Id).QuantityReserved);

        // No sale committed yet, and two PENDING transfers into Allen, both referencing the order.
        Assert.Equal(0, await t.Db.StockMovements.CountAsync(m => m.Type == StockMovementType.Sale));
        var transfers = await t.Db.StockTransfers.Include(x => x.Items).ToListAsync();
        Assert.Equal(2, transfers.Count);
        Assert.All(transfers, x => Assert.Equal(allen.Id, x.ToStoreId));
        Assert.All(transfers, x => Assert.Contains(order.OrderNumber, x.Note));
    }

    [Fact]
    public async Task Fulfil_is_idempotent()
    {
        using var t = new TestDb();
        var user = t.SeedUser();
        var (abuja, allen, ikota) = t.SeedBranches();
        var p = t.SeedProduct();
        t.SetStock(p.Id, allen.Id, 5); // all local → straight sale, no transfers
        var order = t.NewDeliveryOrder(user, "Lagos", "Ikeja", (p, 3));

        var svc = Svc(t);
        await svc.FulfilPaidOrderAsync(order.Id);
        await svc.FulfilPaidOrderAsync(order.Id); // second call must be a no-op

        Assert.Equal(1, await t.Db.StockMovements.CountAsync(m => m.Type == StockMovementType.Sale));
        Assert.Equal(2, t.Inv(p.Id, allen.Id).QuantityOnHand); // sold 3 of 5 once, not twice
    }

    [Fact]
    public async Task Pickup_order_sells_from_chosen_store_without_transfers()
    {
        using var t = new TestDb();
        var user = t.SeedUser();
        var (abuja, allen, ikota) = t.SeedBranches();
        var p = t.SeedProduct();
        t.SetStock(p.Id, ikota.Id, 5);

        var order = t.NewDeliveryOrder(user, "Lagos", "Ajah", (p, 2));
        order.FulfillmentType = FulfillmentType.StorePickup;
        order.PickupStoreId = ikota.Id;
        await t.Db.SaveChangesAsync();

        await Svc(t).FulfilPaidOrderAsync(order.Id);

        var fulfilled = await t.Db.Orders.FindAsync(order.Id);
        Assert.Equal(ikota.Id, fulfilled!.FulfillingStoreId);
        Assert.Equal(OrderStatus.ReadyForPickup, fulfilled.Status);
        Assert.Equal(3, t.Inv(p.Id, ikota.Id).QuantityOnHand);
        Assert.Empty(await t.Db.StockTransfers.ToListAsync());
    }

    // Variant-pool reconciliation: fulfilment must resolve a line to the VARIANT's own inventory row,
    // never the shared product pool — matching StockService's deduction. If the variant has no row at
    // any branch (only the pool holds stock), the order is sold-out → refunded, NOT charged-then-stuck
    // (which is what happened when allocation read the pool but deduction targeted the empty variant row).
    [Fact]
    public async Task Variant_with_no_row_is_sold_out_not_drawn_from_product_pool()
    {
        using var t = new TestDb();
        var user = t.SeedUser();
        var store = t.SeedStore("Allen", "Lagos", "Ikeja");
        var p = t.SeedProduct();
        var variant = new ProductVariant { ProductId = p.Id, Name = "Gold", IsActive = true };
        t.Db.ProductVariants.Add(variant);
        t.Db.SaveChanges();

        // Stock sits ONLY in the product-pool row (ProductVariantId == null); the variant has no row.
        t.SetStock(p.Id, store.Id, onHand: 5);

        // An online order for the VARIANT.
        var order = new Order
        {
            OrderNumber = "T-" + System.Guid.NewGuid().ToString("N")[..10],
            UserId = user.Id, Channel = OrderChannel.Online, FulfillmentType = FulfillmentType.Delivery,
            Status = OrderStatus.Confirmed, Currency = "NGN", Subtotal = p.Price, Total = p.Price,
            PaymentReference = "REF-1",
            DeliveryAddress = new Address { UserId = user.Id, FullName = "Test", Phone = "0800",
                Line1 = "1 St", City = "Ikeja", State = "Lagos", Country = "Nigeria" },
            Items = new List<OrderItem> { new() { ProductId = p.Id, ProductVariantId = variant.Id,
                ProductName = p.Name, VariantName = "Gold", Quantity = 1, UnitPrice = p.Price } }
        };
        t.Db.Orders.Add(order);
        t.Db.SaveChanges();

        var outcome = await Svc(t).FulfilPaidOrderAsync(order.Id);

        Assert.Equal(FulfilOutcome.SoldOut, outcome);
        Assert.Equal(5, t.Inv(p.Id, store.Id).QuantityOnHand);   // pool row untouched — not raided
        Assert.Equal(0, await t.Db.StockMovements.CountAsync(m => m.Type == StockMovementType.Sale));
        var after = await t.Db.Orders.FindAsync(order.Id);
        Assert.True(after!.Status is OrderStatus.Refunded or OrderStatus.Cancelled); // made whole, not stuck
    }

    // ── Minimal stubs for the dependencies the fulfilment service doesn't exercise here ──
    private sealed class FakeEmail : IEmailService
    {
        public Task<bool> SendAsync(string toEmail, string subject, string innerHtml, string? toName = null, System.Threading.CancellationToken ct = default)
            => Task.FromResult(true);
        public Task<string> RenderAsync(string subject, string innerHtml, int? logoHeight = null)
            => Task.FromResult(innerHtml);
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
        public Task<RefundResult> RefundPaymentAsync(RefundPaymentRequest request)
            => Task.FromResult(new RefundResult { Success = true, Supported = true });
    }
}
