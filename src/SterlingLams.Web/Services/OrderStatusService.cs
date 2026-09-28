using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.Controllers;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

public enum OrderStatusOutcome { Applied, SoldOut }

/// <summary>
/// The single path every order status change goes through — the admin Orders screen, the bulk action,
/// and the Logistics board alike — so none can skip the stock deduction, the timeline note, the
/// logistics push or the customer email. Extracted from OrdersController so the Logistics board can
/// dispatch orders under its OWN permission without needing the Orders section granted.
/// </summary>
public interface IOrderStatusService
{
    /// <summary>Moves one (already-loaded, tracked) order to <paramref name="newStatus"/>. Commits it on
    /// its own. Callers must reject <see cref="OrderStatus.Refunded"/> first — refunds run through RefundOrder.</summary>
    Task<OrderStatusOutcome> ApplyAsync(Order order, OrderStatus newStatus, string staff,
        string? paymentMethod = null, string? confirmReason = null);

    /// <summary>Sends (or re-sends) the store-pickup QR pass email and stamps PickupReadyEmailedAt.</summary>
    Task SendPickupReadyEmailAsync(int orderId);
}

public sealed class OrderStatusService : IOrderStatusService
{
    private readonly ApplicationDbContext _db;
    private readonly IOrderFulfilmentService _fulfilment;
    private readonly Logistics.ILogisticsDispatchService _logistics;
    private readonly IEmailService _email;
    private readonly ISettingsService _settings;
    private readonly IWhatsAppService _whatsapp;
    private readonly IAuditService _audit;
    private readonly IHttpContextAccessor _http;
    private readonly LinkGenerator _links;

    public OrderStatusService(ApplicationDbContext db, IOrderFulfilmentService fulfilment,
        Logistics.ILogisticsDispatchService logistics, IEmailService email, ISettingsService settings,
        IWhatsAppService whatsapp, IAuditService audit, IHttpContextAccessor http, LinkGenerator links)
    {
        _db = db;
        _fulfilment = fulfilment;
        _logistics = logistics;
        _email = email;
        _settings = settings;
        _whatsapp = whatsapp;
        _audit = audit;
        _http = http;
        _links = links;
    }

    private string BaseUrl()
    {
        var req = _http.HttpContext?.Request;
        return req == null ? "" : $"{req.Scheme}://{req.Host}";
    }

    private async Task LogAsync(string action, string entityType, string? entityId, string description)
    {
        try { await _audit.LogAsync(action, entityType, entityId, description); } catch { /* auditing must never break the op */ }
    }

    public async Task<OrderStatusOutcome> ApplyAsync(Order order, OrderStatus newStatus, string staff,
        string? paymentMethod = null, string? confirmReason = null)
    {
        var old = order.Status;

        // Confirming an as-yet-unpaid order means the money has arrived out of band — a transfer the
        // shop received, cash/card on pickup, etc. Mark it paid so it reconciles in Finance and can be
        // fulfilled, and record the tender under the channel the staffer chose (defaults to Transfer
        // for the bulk action, which can't prompt).
        if (newStatus == OrderStatus.Confirmed && !order.IsPaid)
        {
            var channel = string.IsNullOrWhiteSpace(paymentMethod) ? "Transfer" : paymentMethod.Trim();
            var reason = string.IsNullOrWhiteSpace(confirmReason) ? "" : $" Reason: {confirmReason.Trim()}";
            order.IsPaid = true;
            order.PaidAt = DateTime.UtcNow;
            order.PaymentProvider = $"Manual ({channel})";
            _db.OrderPayments.Add(new OrderPayment { OrderId = order.Id, Method = channel, Amount = order.Total });
            OrderNotes.AddSystem(_db, order.Id, $"Payment confirmed manually by {staff} — {channel}, ₦{order.Total:N0}.{reason}");
            await _db.SaveChangesAsync();
            await LogAsync("Payment", "Order", order.Id.ToString(),
                $"Manual payment confirm for {order.OrderNumber}: {channel}, ₦{order.Total:N0} by {staff}.{reason}");
        }

        // Deduct stock when staff move an online order forward for the first time. The fulfilment engine
        // allocates from the nearest branch + sets up any inter-branch transfers, and is idempotent (it
        // no-ops once FulfillingStoreId is set), so this is safe even if payment already fulfilled it.
        var needsFulfil = order.Channel == OrderChannel.Online
            && order.FulfillingStoreId == null
            && newStatus is OrderStatus.Confirmed or OrderStatus.Processing
                or OrderStatus.ReadyForPickup or OrderStatus.Shipped or OrderStatus.Delivered;

        if (needsFulfil)
        {
            var outcome = await _fulfilment.FulfilPaidOrderAsync(order.Id);
            await _db.Entry(order).ReloadAsync();
            if (outcome == FulfilOutcome.SoldOut) return OrderStatusOutcome.SoldOut;
            // The engine advances cross-branch orders to Awaiting Transfer (the transfer flow must run)
            // — don't override that. Otherwise honour the status the staff picked.
            if (order.Status != OrderStatus.AwaitingTransfer)
            {
                order.Status = newStatus;
                OrderNotes.AddSystem(_db, order.Id, $"Marked {newStatus} by {staff} (stock deducted).");
            }
            order.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        else
        {
            order.Status = newStatus;
            order.UpdatedAt = DateTime.UtcNow;
            OrderNotes.AddSystem(_db, order.Id, $"Order status changed from {old} to {newStatus} by {staff}.");
            await _db.SaveChangesAsync();
        }

        await LogAsync("Update", "Order", order.Id.ToString(),
            $"Order {order.OrderNumber} status: {old} → {order.Status}");

        // Push the order to the Lagos delivery system once it's a confirmed delivery order. Idempotent.
        await _logistics.PushOrderAsync(order.Id);

        // On dispatch of a delivery order, email the logistics team the "new delivery to fulfil" notice
        // (configurable logistics.notify_email). Only on a real transition to Shipped.
        if (old != order.Status && order.Status == OrderStatus.Shipped
            && order.FulfillmentType == FulfillmentType.Delivery)
        {
            var logiUrl = _links.GetUriByAction(_http.HttpContext!, "Detail", "Orders",
                new { area = "Admin", id = order.Id }, _http.HttpContext!.Request.Scheme, _http.HttpContext!.Request.Host);
            await _fulfilment.NotifyLogisticsDispatchAsync(order.Id, logiUrl);
        }

        // Keep the customer posted as their order reaches each milestone. Only on a real transition
        // (old != new) so re-saving the same status never re-sends.
        if (old != order.Status)
        {
            if (order.Status == OrderStatus.ReadyForPickup
                && order.FulfillmentType == FulfillmentType.StorePickup)
            {
                if (order.PickupReadyEmailedAt == null)
                    await SendPickupReadyEmailAsync(order.Id);
            }
            else if (order.Status is OrderStatus.Confirmed or OrderStatus.Processing or OrderStatus.Shipped
                     or OrderStatus.Delivered or OrderStatus.ReadyForPickup
                     or OrderStatus.Collected or OrderStatus.Cancelled)
            {
                await SendStatusUpdateEmailAsync(order.Id, order.Status);
            }
        }

        return OrderStatusOutcome.Applied;
    }

    /// <summary>The person who bought the order — on a POS sale Order.User is the CASHIER and the buyer
    /// sits in Order.Customer; online, Order.User IS the buyer. Requires User/Customer to be included.</summary>
    private static ApplicationUser? Buyer(Order order) =>
        order.Channel == OrderChannel.Pos ? order.Customer : order.User;

    // Maps an order status to its Email Customizer template key.
    private static string StatusTemplateKey(OrderStatus s) => s switch
    {
        OrderStatus.Processing     => "order_processing",
        OrderStatus.ReadyForPickup => "ready_for_pickup",
        OrderStatus.Shipped        => "order_shipped",
        OrderStatus.Delivered      => "order_delivered",
        OrderStatus.Collected      => "order_collected",
        OrderStatus.Cancelled      => "order_cancelled",
        _                          => "order_confirmed",
    };

    public async Task SendPickupReadyEmailAsync(int orderId)
    {
        var order = await _db.Orders
            .Include(o => o.Items).Include(o => o.PickupStore).Include(o => o.User).Include(o => o.Customer)
            .FirstOrDefaultAsync(o => o.Id == orderId);
        if (order == null || order.FulfillmentType != FulfillmentType.StorePickup) return;
        var buyer = Buyer(order);
        var email = buyer?.Email;
        if (string.IsNullOrWhiteSpace(email)) return;

        if (string.IsNullOrEmpty(order.PickupToken))
        {
            order.PickupToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            await _db.SaveChangesAsync(); // persist the token so the QR pass works even if email send fails
        }

        var baseUrl = BaseUrl();
        var passUrl = $"{baseUrl}/pickup/{order.PickupToken}";
        var qrUrl = $"{passUrl}/qr.png";
        var store = order.PickupStore;
        var firstName = string.IsNullOrWhiteSpace(buyer?.FirstName) ? "there" : buyer!.FirstName;
        string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

        // Subject + intro are editable in the Email Customizer ("Ready for pickup" template).
        var def = EmailCustomizerController.Types.FirstOrDefault(t => t.Key == "ready_for_pickup");
        var subject = await _settings.GetAsync("email.ready_for_pickup.subject",
            def.DefaultSubject ?? $"Your order {order.OrderNumber} is ready for pickup");
        var introText = await _settings.GetAsync("email.ready_for_pickup.intro", def.DefaultIntro ?? "");
        var introHtml = OrderEmailTemplate.ApplyPlaceholders(introText, order.OrderNumber, order.CreatedAt, firstName);

        var items = string.Join("", order.Items.Select(i =>
            $"<tr><td style=\"padding:4px 0;color:#374151\">{Enc(i.ProductName)}{(i.VariantName != null ? " (" + Enc(i.VariantName) + ")" : "")} &times; {i.Quantity}</td>"
            + $"<td style=\"padding:4px 0;text-align:right;color:#111\">₦{i.LineTotal:N0}</td></tr>"));

        var storeBlock = store == null ? "" :
            $"<p style=\"margin:0 0 2px;font-weight:600\">{Enc(store.Name)}</p>"
            + $"<p style=\"margin:0;color:#6b7280;font-size:13px\">{Enc($"{store.Address}, {store.City}, {store.State}".Trim(' ', ','))}</p>"
            + (string.IsNullOrWhiteSpace(store.Phone) ? "" : $"<p style=\"margin:2px 0 0;color:#6b7280;font-size:13px\">{Enc(store.Phone)}</p>");

        var body =
            $"<p>Hi {Enc(firstName)},</p>"
            + $"<p>{introHtml}</p>"
            + $"<div style=\"text-align:center;margin:18px 0\"><img src=\"{qrUrl}\" alt=\"Pickup QR code\" width=\"200\" height=\"200\" style=\"width:200px;height:200px\" /><br/>"
            + $"<span style=\"font:12px monospace;color:#6b7280\">{Enc(order.OrderNumber)}</span></div>"
            + $"<div style=\"text-align:center;margin:0 0 18px\"><a href=\"{passUrl}\" style=\"display:inline-block;background:#ed028b;color:#fff;text-decoration:none;padding:10px 22px;border-radius:8px;font-weight:600\">View pickup pass</a></div>"
            + $"<p style=\"font-weight:600;margin:18px 0 4px\">Pickup location</p>{storeBlock}"
            + $"<table style=\"width:100%;border-collapse:collapse;margin-top:16px;font-size:14px\">{items}"
            + $"<tr><td style=\"padding-top:8px;border-top:1px solid #eee;font-weight:700\">Total</td><td style=\"padding-top:8px;border-top:1px solid #eee;text-align:right;font-weight:700\">₦{order.Total:N0}</td></tr></table>"
            + "<p style=\"color:#6b7280;font-size:13px;margin-top:16px\">Please bring a valid ID. This pass is unique to your order.</p>";

        var sent = await _email.SendAsync(email!, subject, body, buyer?.FullName);
        if (sent)
        {
            order.PickupReadyEmailedAt = DateTime.UtcNow;
            OrderNotes.AddSystem(_db, order.Id, "Ready-for-pickup email with QR pass sent to the customer.");
            await _db.SaveChangesAsync();
        }
    }

    // Customer-facing status-update email (Processing / Shipped / Delivered, etc.). Subject + intro from
    // the Email Customizer; body is the shared compact order summary. Best-effort.
    private async Task SendStatusUpdateEmailAsync(int orderId, OrderStatus status)
    {
        var order = await _db.Orders
            .Include(o => o.Items).Include(o => o.User).Include(o => o.Customer)
            .FirstOrDefaultAsync(o => o.Id == orderId);
        if (order == null) return;
        var buyer = Buyer(order);
        var email = buyer?.Email;
        if (string.IsNullOrWhiteSpace(email)) return;

        var key = StatusTemplateKey(status);
        var def = EmailCustomizerController.Types.FirstOrDefault(t => t.Key == key);
        var subject = await _settings.GetAsync($"email.{key}.subject", def.DefaultSubject ?? "Your order update");
        var introText = await _settings.GetAsync($"email.{key}.intro", def.DefaultIntro ?? "");

        var firstName = string.IsNullOrWhiteSpace(buyer?.FirstName) ? "there" : buyer!.FirstName;
        var introHtml = OrderEmailTemplate.ApplyPlaceholders(introText, order.OrderNumber, order.CreatedAt, firstName);
        // Per-item primary image, made absolute for email clients (Cloudinary URLs already are).
        var pids = order.Items.Select(i => i.ProductId).Distinct().ToList();
        var imgMap = await _db.ProductImages.Where(im => pids.Contains(im.ProductId))
            .GroupBy(im => im.ProductId)
            .Select(g => new { Pid = g.Key, Url = g.OrderByDescending(x => x.IsPrimary).Select(x => x.Url).FirstOrDefault() })
            .ToDictionaryAsync(x => x.Pid, x => x.Url);
        var baseUrl = BaseUrl();
        string? AbsImg(int pid)
        {
            var u = imgMap.GetValueOrDefault(pid);
            if (string.IsNullOrWhiteSpace(u)) return null;
            return u.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? u
                 : (string.IsNullOrEmpty(baseUrl) ? null : baseUrl + "/" + u.TrimStart('/'));
        }
        var items = order.Items
            .Select(i => new OrderEmailTemplate.Item(i.ProductName, i.VariantName, i.Quantity, i.LineTotal, AbsImg(i.ProductId)))
            .ToList();

        var body = OrderEmailTemplate.BuildStatusUpdate(subject, introHtml, order.OrderNumber, items, order.Total);
        var sent = await _email.SendAsync(email!, subject, body, buyer?.FullName);
        if (sent)
        {
            OrderNotes.AddSystem(_db, order.Id, $"'{def.Label ?? status.ToString()}' status email sent to the customer.");
            await _db.SaveChangesAsync();
        }

        // Fire the matching WhatsApp alongside the email (own scope, never throws, gated by the
        // whatsapp.notify.* toggle + a customer phone). Fire-and-forget so it doesn't slow the UI.
        var waEvent = status switch
        {
            OrderStatus.ReadyForPickup => (WhatsAppOrderEvent?)WhatsAppOrderEvent.ReadyForPickup,
            OrderStatus.Shipped        => WhatsAppOrderEvent.Shipped,
            OrderStatus.Delivered      => WhatsAppOrderEvent.Delivered,
            _ => null,
        };
        if (waEvent is { } ev) _ = _whatsapp.NotifyOrderAsync(orderId, ev);
    }
}
