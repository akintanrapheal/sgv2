using Microsoft.AspNetCore.Mvc;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services.Odoo;
using SterlingLams.Web.Services.Payment;
using Microsoft.EntityFrameworkCore;
using System.IO;
using System.Text;

namespace SterlingLams.Web.Controllers;

[ApiController]
[Route("webhooks")]
public class WebhooksController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IPaymentService _payment;
    private readonly IOdooService _odoo;
    private readonly ILogger<WebhooksController> _logger;

    public WebhooksController(
        ApplicationDbContext db,
        IPaymentService payment,
        IOdooService odoo,
        ILogger<WebhooksController> logger)
    {
        _db = db;
        _payment = payment;
        _odoo = odoo;
        _logger = logger;
    }

    [HttpPost("paystack")]
    public async Task<IActionResult> PaystackWebhook()
    {
        // Read raw body for HMAC validation
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var payload = await reader.ReadToEndAsync();

        var signature = Request.Headers["x-paystack-signature"].FirstOrDefault() ?? string.Empty;

        if (!await _payment.ValidateWebhookAsync(payload, signature))
        {
            _logger.LogWarning("Invalid Paystack webhook signature");
            return Unauthorized();
        }

        // Parse event type
        using var doc = System.Text.Json.JsonDocument.Parse(payload);
        var root = doc.RootElement;

        var eventType = root.TryGetProperty("event", out var evtProp) ? evtProp.GetString() : null;
        _logger.LogInformation("Paystack webhook event: {Event}", eventType);

        if (eventType == "charge.success")
        {
            var data = root.GetProperty("data");
            var reference = data.GetProperty("reference").GetString();
            var amount = data.GetProperty("amount").GetDecimal() / 100m;

            var order = await _db.Orders
                .FirstOrDefaultAsync(o => o.PaymentReference == reference
                    || (o.OrderNumber != null && reference != null && reference.Contains(o.OrderNumber)));

            if (order != null && !order.IsPaid)
            {
                order.IsPaid = true;
                order.PaidAt = DateTime.UtcNow;
                order.Status = OrderStatus.Confirmed;
                order.PaymentReference = reference;
                order.PaymentProvider = "Paystack";
                await _db.SaveChangesAsync();

                // Push to Odoo async (fire & forget with error logging)
                _ = Task.Run(async () =>
                {
                    try { await _odoo.ConfirmSaleOrderAsync(order.OdooSaleOrderId ?? 0); }
                    catch (Exception ex) { _logger.LogError(ex, "Failed to confirm Odoo order for {OrderNumber}", order.OrderNumber); }
                });

                _logger.LogInformation("Order {OrderNumber} marked as paid via webhook", order.OrderNumber);
            }
        }

        return Ok();
    }
}
