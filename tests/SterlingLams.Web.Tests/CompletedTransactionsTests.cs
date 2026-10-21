using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using SterlingLams.Web.Areas.Admin.Controllers;
using SterlingLams.Web.Models.Domain;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>Finance → Completed Transactions is what Finance uses to check cashier end-of-day:
/// website totals exclude the delivery fee (merchandise only), and voided till sales appear as a
/// negative row dated at the void time so the day nets out.</summary>
public class CompletedTransactionsTests
{
    private static async Task<FinanceController.CompletedTxnVm> LoadAsync(TestDb t)
    {
        var ctrl = new FinanceController(t.Db, null!, null!, null!, null!);
        var from = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");
        var to = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");
        var result = await ctrl.CompletedTransactions(from, to, null, null, null, null, 1);
        return (FinanceController.CompletedTxnVm)((ViewResult)result).ViewData.Model!;
    }

    [Fact]
    public async Task Website_order_total_excludes_delivery_fee()
    {
        using var t = new TestDb();
        var (_, allen, _) = t.SeedBranches();
        var user = t.SeedUser();
        var p = t.SeedProduct(5000m);

        t.Db.Orders.Add(new Order
        {
            OrderNumber = "W-DEL", UserId = user.Id, Channel = OrderChannel.Online,
            FulfillmentType = FulfillmentType.Delivery, FulfillingStoreId = allen.Id,
            // Website orders appear on Completed Transactions on their PACKED day (like the EOD report).
            Status = OrderStatus.Processing, IsPaid = true, PaidAt = DateTime.UtcNow, PackedAt = DateTime.UtcNow,
            PaymentProvider = "Paystack", Currency = "NGN",
            Subtotal = 5000m, DeliveryFee = 1500m, Total = 6500m,
            Items = { new OrderItem { ProductId = p.Id, ProductName = p.Name, Quantity = 1, UnitPrice = 5000m } }
        });
        await t.Db.SaveChangesAsync();

        var vm = await LoadAsync(t);

        var row = vm.Rows.Single(r => !r.IsRefund && !r.IsVoid);
        Assert.Equal(5000m, row.Total);   // ₦6,500 total − ₦1,500 delivery = product amount only
        Assert.Equal(5000m, vm.Total);    // the Sales summary excludes delivery too
    }

    [Fact]
    public async Task Voided_till_sale_shows_as_a_negative_row_and_nets_to_zero()
    {
        using var t = new TestDb();
        var (_, allen, _) = t.SeedBranches();
        var user = t.SeedUser();
        var p = t.SeedProduct(3000m);
        var reg = new Register { Name = "Allen Pos 1", StoreId = allen.Id, IsActive = true };
        t.Db.Registers.Add(reg);
        await t.Db.SaveChangesAsync();

        t.Db.Orders.Add(new Order
        {
            OrderNumber = "P-VOID", UserId = user.Id, Channel = OrderChannel.Pos, RegisterId = reg.Id,
            FulfillingStoreId = allen.Id, Status = OrderStatus.Delivered, IsPaid = true,
            PaidAt = DateTime.UtcNow.AddMinutes(-20), PaymentProvider = "Cash", Currency = "NGN",
            Subtotal = 3000m, Total = 3000m,
            VoidedAt = DateTime.UtcNow.AddMinutes(-5), VoidedByName = "Mgr", VoidReason = "Wrong item",
            Items = { new OrderItem { ProductId = p.Id, ProductName = p.Name, Quantity = 1, UnitPrice = 3000m } }
        });
        await t.Db.SaveChangesAsync();

        var vm = await LoadAsync(t);

        // The original sale still shows (positive), and a void row shows the reversal (negative).
        Assert.Contains(vm.Rows, r => !r.IsVoid && !r.IsRefund && r.Total == 3000m);
        var voidRow = vm.Rows.Single(r => r.IsVoid);
        Assert.Equal(-3000m, voidRow.Total);
        Assert.Equal("Void", voidRow.Tender);
        Assert.Equal("Wrong item", voidRow.DiscountReason);

        Assert.Equal(3000m, vm.VoidsTotal);
        Assert.Equal(0m, vm.Net);   // sale (+3,000) − void (3,000) = 0 for the day
    }

    [Fact]
    public async Task Voided_website_order_nets_to_zero_with_delivery_excluded()
    {
        using var t = new TestDb();
        var (_, allen, _) = t.SeedBranches();
        var user = t.SeedUser();
        var p = t.SeedProduct(5000m);

        t.Db.Orders.Add(new Order
        {
            OrderNumber = "W-VOID", UserId = user.Id, Channel = OrderChannel.Online,
            FulfillmentType = FulfillmentType.Delivery, FulfillingStoreId = allen.Id,
            // Packed (so it earned a positive row on its packed day) then voided — the two net to zero.
            Status = OrderStatus.Cancelled, IsPaid = true, PaidAt = DateTime.UtcNow.AddMinutes(-20),
            PackedAt = DateTime.UtcNow.AddMinutes(-15),
            PaymentProvider = "Paystack", Currency = "NGN",
            Subtotal = 5000m, DeliveryFee = 1500m, Total = 6500m,
            VoidedAt = DateTime.UtcNow.AddMinutes(-5), VoidedByName = "Mgr", VoidReason = "Test order",
            Items = { new OrderItem { ProductId = p.Id, ProductName = p.Name, Quantity = 1, UnitPrice = 5000m } }
        });
        await t.Db.SaveChangesAsync();

        var vm = await LoadAsync(t);

        // Positive sale row (product only, ₦5,000) + a negative void row (−₦5,000, delivery excluded) → nets to 0.
        Assert.Contains(vm.Rows, r => !r.IsVoid && !r.IsRefund && r.Total == 5000m);
        var voidRow = vm.Rows.Single(r => r.IsVoid);
        Assert.Equal(-5000m, voidRow.Total);
        Assert.Equal("Void", voidRow.Tender);
        Assert.Equal(5000m, vm.VoidsTotal);
        Assert.Equal(0m, vm.Net);
    }

    [Fact]
    public async Task Website_filter_excludes_void_rows()
    {
        using var t = new TestDb();
        var (_, allen, _) = t.SeedBranches();
        var user = t.SeedUser();
        var p = t.SeedProduct(3000m);
        var reg = new Register { Name = "Allen Pos 1", StoreId = allen.Id, IsActive = true };
        t.Db.Registers.Add(reg);
        await t.Db.SaveChangesAsync();
        t.Db.Orders.Add(new Order
        {
            OrderNumber = "P-VOID2", UserId = user.Id, Channel = OrderChannel.Pos, RegisterId = reg.Id,
            FulfillingStoreId = allen.Id, Status = OrderStatus.Delivered, IsPaid = true,
            PaidAt = DateTime.UtcNow.AddMinutes(-20), PaymentProvider = "Cash", Currency = "NGN",
            Subtotal = 3000m, Total = 3000m,
            VoidedAt = DateTime.UtcNow.AddMinutes(-5), VoidedByName = "Mgr", VoidReason = "x",
            Items = { new OrderItem { ProductId = p.Id, ProductName = p.Name, Quantity = 1, UnitPrice = 3000m } }
        });
        await t.Db.SaveChangesAsync();

        var ctrl = new FinanceController(t.Db, null!, null!, null!, null!);
        var from = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");
        var to = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");
        var result = await ctrl.CompletedTransactions(from, to, null, null, "Online", null, 1);
        var vm = (FinanceController.CompletedTxnVm)((ViewResult)result).ViewData.Model!;

        Assert.DoesNotContain(vm.Rows, r => r.IsVoid);   // voids are POS — none under the Website filter
    }

    [Fact]
    public async Task EndOfDay_website_amounts_exclude_delivery()
    {
        using var t = new TestDb();
        var (abuja, _, _) = t.SeedBranches();
        var user = t.SeedUser();
        var p = t.SeedProduct(20000m);

        // A website order fulfilled by Abuja, PACKED today (EOD counts website orders on the packed day).
        t.Db.Orders.Add(new Order
        {
            OrderNumber = "W-EOD", UserId = user.Id, Channel = OrderChannel.Online,
            FulfillmentType = FulfillmentType.Delivery, FulfillingStoreId = abuja.Id,
            Status = OrderStatus.Processing, IsPaid = true, PaidAt = DateTime.UtcNow,
            PackedAt = DateTime.UtcNow, PackedByName = "Dorathy", PaymentProvider = "Paystack",
            Currency = "NGN", Subtotal = 20000m, DeliveryFee = 5000m, Total = 25000m,
            Items = { new OrderItem { ProductId = p.Id, ProductName = p.Name, Quantity = 1, UnitPrice = 20000m } }
        });
        await t.Db.SaveChangesAsync();

        var ctrl = new FinanceController(t.Db, null!, null!, null!, null!);
        var day = DateTime.UtcNow.AddHours(1).ToString("yyyy-MM-dd");   // Lagos day
        var result = await ctrl.EndOfDay(day, null);
        var vm = (FinanceController.EndOfDayVm)((ViewResult)result).ViewData.Model!;

        var abujaRow = vm.Rows.Single(r => r.StoreId == abuja.Id);
        Assert.Equal(20000m, abujaRow.Ops.WebsiteAmount);        // 25,000 − 5,000 delivery = product only
        Assert.Equal(20000m, abujaRow.Ops.PackedWebsiteAmount);
        Assert.Equal(1, abujaRow.Ops.PackedWebsiteOrders);
    }

    [Fact]
    public async Task Split_online_order_credits_each_item_to_its_supplying_branch()
    {
        // SGW-30516: a pickup order at Ikota where Allen packed one item and transferred it to Ikota
        // to merge. Each item must show under the branch that actually supplied it, not the whole
        // order under the pickup branch — so Allen sees its sale and it lands in Allen's end-of-day.
        using var t = new TestDb();
        var (_, allen, ikota) = t.SeedBranches();
        var user = t.SeedUser();
        var earring = t.SeedProduct(9950m);    // packed/supplied by Allen
        var rest = t.SeedProduct(23900m);      // supplied by Ikota (the pickup branch)

        t.Db.Orders.Add(new Order
        {
            OrderNumber = "SGW-SPLIT", UserId = user.Id, Channel = OrderChannel.Online,
            FulfillmentType = FulfillmentType.StorePickup, PickupStoreId = ikota.Id, FulfillingStoreId = ikota.Id,
            Status = OrderStatus.Processing, IsPaid = true, PaidAt = DateTime.UtcNow, PackedAt = DateTime.UtcNow,
            PaymentProvider = "Paystack", Currency = "NGN",
            Subtotal = 33850m, DeliveryFee = 0m, Total = 33850m,
            Items =
            {
                new OrderItem { ProductId = earring.Id, ProductName = earring.Name, Quantity = 1, UnitPrice = 9950m, FulfilledFromStoreId = allen.Id },
                new OrderItem { ProductId = rest.Id, ProductName = rest.Name, Quantity = 1, UnitPrice = 23900m, FulfilledFromStoreId = ikota.Id },
            }
        });
        await t.Db.SaveChangesAsync();

        var vm = await LoadAsync(t);

        // Two sale rows — one per supplying branch — not one ₦33,850 row under Ikota.
        var sales = vm.Rows.Where(r => !r.IsRefund && !r.IsVoid).ToList();
        Assert.Equal(2, sales.Count);
        Assert.Equal(9950m, sales.Single(r => r.Location == allen.Name).Total);
        Assert.Equal(23900m, sales.Single(r => r.Location == ikota.Name).Total);
        Assert.Equal(33850m, vm.Total);   // the split still sums to the order's merchandise total

        // Filtering by Allen isolates Allen's packed item only.
        var ctrl = new FinanceController(t.Db, null!, null!, null!, null!);
        var from = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");
        var to = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");
        var result = await ctrl.CompletedTransactions(from, to, allen.Id, null, null, null, 1);
        var allenVm = (FinanceController.CompletedTxnVm)((ViewResult)result).ViewData.Model!;
        var allenSale = allenVm.Rows.Single(r => !r.IsRefund && !r.IsVoid);
        Assert.Equal(allen.Name, allenSale.Location);
        Assert.Equal(9950m, allenSale.Total);
        Assert.Equal(9950m, allenVm.Total);
    }

    [Fact]
    public async Task Legacy_order_without_per_item_store_falls_back_to_pickup_branch()
    {
        // Orders packed before this feature have no FulfilledFromStoreId — they must still show as one
        // row under the pickup/fulfilling branch (not vanish, not split).
        using var t = new TestDb();
        var (_, _, ikota) = t.SeedBranches();
        var user = t.SeedUser();
        var p = t.SeedProduct(5000m);

        t.Db.Orders.Add(new Order
        {
            OrderNumber = "SGW-LEGACY", UserId = user.Id, Channel = OrderChannel.Online,
            FulfillmentType = FulfillmentType.StorePickup, PickupStoreId = ikota.Id, FulfillingStoreId = ikota.Id,
            Status = OrderStatus.Processing, IsPaid = true, PaidAt = DateTime.UtcNow, PackedAt = DateTime.UtcNow,
            PaymentProvider = "Paystack", Currency = "NGN",
            Subtotal = 5000m, Total = 5000m,
            Items = { new OrderItem { ProductId = p.Id, ProductName = p.Name, Quantity = 1, UnitPrice = 5000m } }  // FulfilledFromStoreId null
        });
        await t.Db.SaveChangesAsync();

        var vm = await LoadAsync(t);

        var row = vm.Rows.Single(r => !r.IsRefund && !r.IsVoid);
        Assert.Equal(ikota.Name, row.Location);
        Assert.Equal(5000m, row.Total);
    }

    [Fact]
    public async Task Paid_but_unpacked_website_order_does_not_appear()
    {
        using var t = new TestDb();
        var (_, allen, _) = t.SeedBranches();
        var user = t.SeedUser();
        var p = t.SeedProduct(5000m);

        // Paid online order that hasn't been packed/fulfilled yet — not a completed transaction until it is.
        t.Db.Orders.Add(new Order
        {
            OrderNumber = "W-UNPACKED", UserId = user.Id, Channel = OrderChannel.Online,
            FulfillmentType = FulfillmentType.Delivery, FulfillingStoreId = allen.Id,
            Status = OrderStatus.Processing, IsPaid = true, PaidAt = DateTime.UtcNow,
            PaymentProvider = "Paystack", Currency = "NGN",
            Subtotal = 5000m, DeliveryFee = 1500m, Total = 6500m,
            Items = { new OrderItem { ProductId = p.Id, ProductName = p.Name, Quantity = 1, UnitPrice = 5000m } }
        });
        await t.Db.SaveChangesAsync();

        var vm = await LoadAsync(t);

        Assert.Empty(vm.Rows);       // appears only once packed
        Assert.Equal(0m, vm.Total);
    }
}
