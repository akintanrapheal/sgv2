using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers;

/// <summary>
/// Logistics board: the online orders that need delivering or collecting — POS in-store sales are
/// excluded (Channel == Online only). The branch packs an order at the POS; from here logistics marks
/// it "Out for delivery" (which fires the customer's "on its way" email), then "Delivered". Store-pickup
/// orders are shown here too. Runs under its OWN "Logistics" permission — dispatch goes through the
/// shared <see cref="IOrderStatusService"/>, so a logistics-only role never needs the Orders section.
/// </summary>
public class LogisticsController : AdminBaseController
{
    protected override string Section => "Logistics";

    private readonly ApplicationDbContext _db;
    private readonly IOrderStatusService _orderStatus;
    public LogisticsController(ApplicationDbContext db, IOrderStatusService orderStatus)
    {
        _db = db;
        _orderStatus = orderStatus;
    }

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

    // ── Dispatch actions ──────────────────────────────────────────────────────────
    // All go through the shared order-status engine (stock, notes, logistics push, customer email),
    // but under the LOGISTICS permission — so a logistics-only role never needs the Orders section.
    // Each is guarded to the fulfilment type it applies to and comes back to the same board tab.

    // Delivery: mark a packed order out for delivery (→ Shipped). This is what emails the customer
    // "your order is on its way".
    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Dispatch(int id, string tab = "dispatch") =>
        ApplyAsync(id, OrderStatus.Shipped, tab, requireDelivery: true, "marked out for delivery");

    // Delivery: mark it delivered.
    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> MarkDelivered(int id, string tab = "out") =>
        ApplyAsync(id, OrderStatus.Delivered, tab, requireDelivery: true, "marked delivered");

    // Store pickup: email the customer their order is ready to collect (→ ReadyForPickup, QR pass).
    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> NotifyPickup(int id, string tab = "pickup") =>
        ApplyAsync(id, OrderStatus.ReadyForPickup, tab, requireDelivery: false, "flagged ready for pickup");

    // Store pickup: mark it collected.
    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> MarkCollected(int id, string tab = "pickup") =>
        ApplyAsync(id, OrderStatus.Collected, tab, requireDelivery: false, "marked collected");

    private async Task<IActionResult> ApplyAsync(int id, OrderStatus newStatus, string tab, bool requireDelivery, string verb)
    {
        var order = await _db.Orders.FindAsync(id);
        // Website orders only, and the right fulfilment type for the action — never a POS in-store sale.
        if (order == null || order.Channel != OrderChannel.Online
            || order.FulfillmentType != (requireDelivery ? FulfillmentType.Delivery : FulfillmentType.StorePickup))
        {
            TempData["Error"] = "That order can't be handled from the Logistics board.";
            return RedirectToAction(nameof(Index), new { tab });
        }

        var staff = await CurrentStaffNameAsync();
        await _orderStatus.ApplyAsync(order, newStatus, staff);
        TempData["Success"] = $"Order {order.OrderNumber} {verb}.";
        return RedirectToAction(nameof(Index), new { tab });
    }

    // Display name ("First Last", else username) of the signed-in staff member — for order notes.
    private async Task<string> CurrentStaffNameAsync()
    {
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrEmpty(id))
        {
            var u = await _db.Users.Where(x => x.Id == id)
                .Select(x => new { x.FirstName, x.LastName, x.UserName }).FirstOrDefaultAsync();
            if (u != null)
            {
                var name = $"{u.FirstName} {u.LastName}".Trim();
                if (!string.IsNullOrWhiteSpace(name)) return name;
                if (!string.IsNullOrWhiteSpace(u.UserName)) return u.UserName;
            }
        }
        return User.Identity?.Name ?? "staff";
    }
}
