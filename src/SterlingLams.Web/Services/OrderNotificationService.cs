using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

/// <summary>
/// Sends the "new order" emails for a paid order — the rich customer confirmation and the admin
/// new-order alert. Extracted from CheckoutController so the same email flow can run from a
/// background service (the payment-reconciliation sweep / the pre-cancel verify) when a payment
/// is captured without a browser request in play. Best-effort: never throws.
/// </summary>
public interface IOrderNotificationService
{
    Task SendNewOrderEmailsAsync(int orderId, CancellationToken ct = default);
}

public class OrderNotificationService : IOrderNotificationService
{
    private readonly ApplicationDbContext _db;
    private readonly ISettingsService _settings;
    private readonly IConfiguration _config;
    private readonly DeliveryZoneService _zones;
    private readonly IEmailService _email;
    private readonly IWhatsAppService _whatsapp;
    private readonly ILogger<OrderNotificationService> _logger;

    public OrderNotificationService(
        ApplicationDbContext db,
        ISettingsService settings,
        IConfiguration config,
        DeliveryZoneService zones,
        IEmailService email,
        IWhatsAppService whatsapp,
        ILogger<OrderNotificationService> logger)
    {
        _db = db;
        _settings = settings;
        _config = config;
        _zones = zones;
        _email = email;
        _whatsapp = whatsapp;
        _logger = logger;
    }

    public async Task SendNewOrderEmailsAsync(int orderId, CancellationToken ct = default)
    {
        try
        {
            var order = await _db.Orders.Include(o => o.Items)
                .Include(o => o.DeliveryAddress).Include(o => o.PickupStore).Include(o => o.User)
                .FirstOrDefaultAsync(o => o.Id == orderId, ct);
            if (order == null) return;

            var customerEmail = order.User?.Email
                ?? await _db.Users.Where(u => u.Id == order.UserId).Select(u => u.Email).FirstOrDefaultAsync(ct);

            // Customer confirmation — rich WooCommerce-style layout (editable in Email Customizer).
            if (!string.IsNullOrWhiteSpace(customerEmail)
                && await _settings.GetBoolAsync("notifications.order_confirmed", true))
            {
                var subject = await _settings.GetAsync("email.order_confirmed.subject", "Your order is being processed");
                var intro = await _settings.GetAsync("email.order_confirmed.intro",
                    "Your order {order} ({date}) has been received and is now being processed.");

                // Per-item image — the chosen variant's own picture when set, else the product primary.
                var img = await OrderEmailImages.ResolverAsync(_db, order.Items, _config["App:BaseUrl"]);

                var items = order.Items.Select(i =>
                    new OrderEmailTemplate.Item(
                        i.ProductName, i.VariantName, i.Quantity, i.LineTotal, img(i), i.ProductSku)).ToList();

                var custName = !string.IsNullOrWhiteSpace(order.ContactName) ? order.ContactName!.Trim() : (order.User?.FullName ?? order.DeliveryAddress?.FullName ?? "");
                var (billing, shipping, pickupLabel) = OrderEmailTemplate.AddressBlocksFor(order, custName, customerEmail);

                // Glams-branded shipping line, e.g. "₦4,000.00 via Glams Priority Delivery (within 48 hrs)".
                var shippingLabel = await _zones.EmailShippingLabelAsync(order, pickupLabel);

                var introHtml = OrderEmailTemplate.ApplyPlaceholders(
                    intro, "#" + order.OrderNumber, order.CreatedAt, custName);
                var body = OrderEmailTemplate.Build(
                    heading: subject,
                    introHtml: introHtml,
                    orderNumber: order.OrderNumber,
                    orderDate: order.CreatedAt,
                    items: items,
                    subtotal: order.Subtotal,
                    shippingLabel: shippingLabel,
                    total: order.Total,
                    // Friendly payment line like the old site: the Paystack channels the customer could use.
                    paymentMethod: (order.PaymentProvider ?? "").Contains("Paystack", StringComparison.OrdinalIgnoreCase)
                        ? "Debit/Credit Card, Bank Transfer, USSD, Opay"
                        : (string.IsNullOrWhiteSpace(order.PaymentProvider) ? "—" : order.PaymentProvider),
                    billingLines: billing,
                    shippingLines: shipping);
                await _email.SendAsync(customerEmail!, subject, body, ct: ct);
            }

            // WhatsApp order confirmation — self-gated by whatsapp.notify.order_confirmed + a customer
            // phone, independent of the email setting above. Fire-and-forget (own scope, never throws).
            _ = _whatsapp.NotifyOrderAsync(order.Id, WhatsAppOrderEvent.OrderConfirmed);

            // Admin new-order alert
            if (await _settings.GetBoolAsync("notifications.new_order", true))
            {
                // New-order alert recipients: the dedicated new-order list, falling back to the
                // general admin email when that list is left blank.
                var adminEmail = await _settings.GetAsync("notifications.new_order_emails", "");
                if (string.IsNullOrWhiteSpace(adminEmail))
                    adminEmail = await _settings.GetAsync("notifications.admin_email", "");
                if (!string.IsNullOrWhiteSpace(adminEmail))
                {
                    // Rich admin alert — same layout as the customer confirmation (images, colour,
                    // quantities, billing + shipping, pickup vs delivery). Subject/intro editable in
                    // the Email Customizer ("New order alert (admin)").
                    var adminSubjectT = await _settings.GetAsync("email.new_order_admin.subject", "New order {order}");
                    var adminSubject = adminSubjectT.Replace("{order}", order.OrderNumber) + $" — ₦{order.Total:N0}";
                    var adminBody = await BuildAdminOrderAlertAsync(order, customerEmail);
                    var fromAlerts = await _settings.GetAsync("email.from_alerts", "");
                    foreach (var addr in SterlingLams.Web.Infrastructure.EmailRecipients.Split(adminEmail))
                        await _email.SendAsync(addr, adminSubject, adminBody, ct: ct, fromOverride: fromAlerts);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed sending order emails for order {OrderId}", orderId);
        }
    }

    // Rich "new order received" admin body: mirrors the customer confirmation (product images, colour,
    // quantities, billing + shipping, or "Pickup at …" for store pickup). Subject/intro editable in the
    // Email Customizer ("New order alert (admin)").
    private async Task<string> BuildAdminOrderAlertAsync(Order order, string? customerEmail)
    {
        var img = await OrderEmailImages.ResolverAsync(_db, order.Items, _config["App:BaseUrl"]);
        var items = order.Items.Select(i => new OrderEmailTemplate.Item(
            i.ProductName, i.VariantName, i.Quantity, i.LineTotal, img(i), i.ProductSku)).ToList();

        var custName = !string.IsNullOrWhiteSpace(order.ContactName) ? order.ContactName!.Trim() : (order.User?.FullName ?? order.DeliveryAddress?.FullName ?? "");
        var (billing, shipping, pickupLabel) = OrderEmailTemplate.AddressBlocksFor(order, custName, customerEmail);
        var shippingLabel = await _zones.EmailShippingLabelAsync(order, pickupLabel);

        var subjectT = await _settings.GetAsync("email.new_order_admin.subject", "New order {order}");
        var introT = await _settings.GetAsync("email.new_order_admin.intro",
            "A new order has come in — full details below. View it in the admin dashboard under Orders.");
        var heading = subjectT.Replace("{order}", order.OrderNumber);
        var introHtml = OrderEmailTemplate.ApplyPlaceholders(
                introT, "#" + order.OrderNumber, order.CreatedAt, custName)
            + (string.IsNullOrWhiteSpace(customerEmail) ? ""
               : $"<br/><span style=\"color:#78716c;font-size:13px;\">From {System.Net.WebUtility.HtmlEncode(customerEmail)}</span>");

        return OrderEmailTemplate.Build(
            heading: heading, introHtml: introHtml, orderNumber: order.OrderNumber, orderDate: order.CreatedAt,
            items: items, subtotal: order.Subtotal, shippingLabel: shippingLabel, total: order.Total,
            paymentMethod: order.PaymentProvider ?? "—", billingLines: billing, shippingLines: shipping);
    }
}
