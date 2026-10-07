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
            Status = OrderStatus.Processing, IsPaid = true, PaidAt = DateTime.UtcNow,
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
}
