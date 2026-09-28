using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Admin.Controllers;

/// <summary>
/// Logistics board: the online orders that need delivering or collecting — POS in-store sales are
/// excluded (Channel == Online only). The branch packs an order at the POS; from here logistics marks
/// it "Out for delivery" (which fires the customer's "on its way" email via OrdersController.UpdateStatus),
/// then "Delivered". Store-pickup orders are shown here too for oversight.
/// </summary>
public class LogisticsController : AdminBaseController
{
    protected override string Section => "Logistics";

    private readonly ApplicationDbContext _db;
    public LogisticsController(ApplicationDbContext db) => _db = db;

    public sealed class Row
    {
        public int Id { get; set; }
        public string OrderNumber { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public string Customer { get; set; } = "";
        public string? Phone { get; set; }
        public bool IsDelivery { get; set; }
        public string Place { get; set; } = "";
        public string? DeliveryType { get; set; }
        public string Branch { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTime? PackedAt { get; set; }
        public string? PackedBy { get; set; }
        public int ItemCount { get; set; }
        public decimal Total { get; set; }
        public bool PickupNotified { get; set; }
    }

    public sealed class PageVm
    {
        public string Tab { get; set; } = "dispatch";
        public List<Row> Rows { get; set; } = new();
        public int DispatchCount { get; set; }
        public int OutCount { get; set; }
        public int PickupCount { get; set; }
        public int DeliveredCount { get; set; }
    }

    public async Task<IActionResult> Index(string tab = "dispatch")
    {
        ViewData["Title"] = "Logistics";

        // Online customer orders only — POS in-store sales (Channel == Pos) never appear here.
        var baseQ = _db.Orders.Where(o => o.Channel == OrderChannel.Online
            && (o.FulfillmentType == FulfillmentType.Delivery || o.FulfillmentType == FulfillmentType.StorePickup));

        // Counts for the tab badges.
        var vm = new PageVm { Tab = tab };
        vm.DispatchCount = await baseQ.CountAsync(o => o.FulfillmentType == FulfillmentType.Delivery
            && o.PackedAt != null && (o.Status == OrderStatus.Confirmed || o.Status == OrderStatus.Processing));
        vm.OutCount = await baseQ.CountAsync(o => o.FulfillmentType == FulfillmentType.Delivery && o.Status == OrderStatus.Shipped);
        vm.DeliveredCount = await baseQ.CountAsync(o => o.FulfillmentType == FulfillmentType.Delivery && o.Status == OrderStatus.Delivered);
        vm.PickupCount = await baseQ.CountAsync(o => o.FulfillmentType == FulfillmentType.StorePickup
            && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Refunded && o.Status != OrderStatus.Collected);

        IQueryable<Order> q = tab switch
        {
            "out"       => baseQ.Where(o => o.FulfillmentType == FulfillmentType.Delivery && o.Status == OrderStatus.Shipped),
            "delivered" => baseQ.Where(o => o.FulfillmentType == FulfillmentType.Delivery && o.Status == OrderStatus.Delivered),
            "pickup"    => baseQ.Where(o => o.FulfillmentType == FulfillmentType.StorePickup
                                && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Refunded),
            _           => baseQ.Where(o => o.FulfillmentType == FulfillmentType.Delivery
                                && o.PackedAt != null && (o.Status == OrderStatus.Confirmed || o.Status == OrderStatus.Processing)),
        };

        vm.Rows = await q
            .OrderBy(o => o.PackedAt ?? o.CreatedAt)
            .Select(o => new Row
            {
                Id = o.Id,
                OrderNumber = o.OrderNumber,
                CreatedAt = o.CreatedAt,
                Customer = ((o.User.FirstName ?? "") + " " + (o.User.LastName ?? "")).Trim(),
                Phone = o.User.PhoneNumber,
                IsDelivery = o.FulfillmentType == FulfillmentType.Delivery,
                Place = o.FulfillmentType == FulfillmentType.StorePickup
                    ? (o.PickupStore != null ? o.PickupStore.Name : "Store pickup")
                    : (o.DeliveryAddress != null ? (o.DeliveryAddress.City + ", " + o.DeliveryAddress.State) : ""),
                DeliveryType = o.DeliveryType,
                Branch = o.FulfillingStore != null ? o.FulfillingStore.Name
                        : (o.PickupStore != null ? o.PickupStore.Name : ""),
                Status = o.Status.ToString(),
                PackedAt = o.PackedAt,
                PackedBy = o.PackedByName,
                ItemCount = o.Items.Sum(i => i.Quantity),
                Total = o.Total,
                PickupNotified = o.PickupReadyEmailedAt != null,
            })
            .ToListAsync();

        return View(vm);
    }
}
