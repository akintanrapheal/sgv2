using Microsoft.AspNetCore.Mvc;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
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
    private readonly SterlingLams.Web.Services.IPaymentCaptureService _capture;
    private readonly SterlingLams.Web.Services.Logistics.ILogisticsDispatchService _logistics;
    private readonly ISubscriptionPaymentService _subPay;
    private readonly ILogger<WebhooksController> _logger;

    private readonly SterlingLams.Web.Services.IAuditService _audit;

    public WebhooksController(
        ApplicationDbContext db,
        IPaymentService payment,
        SterlingLams.Web.Services.IPaymentCaptureService capture,
        SterlingLams.Web.Services.Logistics.ILogisticsDispatchService logistics,
        ISubscriptionPaymentService subPay,
        SterlingLams.Web.Services.IAuditService audit,
        ILogger<WebhooksController> logger)
    {
        _db = db;
        _payment = payment;
        _capture = capture;
        _logistics = logistics;
        _subPay = subPay;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>
    /// Paystack webhook for the API-connector SUBSCRIPTION (developer's Paystack account). Validated
    /// with the developer key. On a verified charge.success for a subscription reference, activates the
    /// subscription — so activation still happens even if the admin closed the tab before the redirect.
    /// Idempotent. Configure this URL (/webhooks/subscription) in the DEVELOPER's Paystack dashboard.
    /// </summary>
    [HttpPost("subscription")]
    public async Task<IActionResult> SubscriptionWebhook()
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var payload = await reader.ReadToEndAsync();
        var signature = Request.Headers["x-paystack-signature"].FirstOrDefault() ?? string.Empty;

        if (!await _subPay.ValidateWebhookAsync(payload, signature))
        {
            _logger.LogWarning("Invalid subscription Paystack webhook signature");
            return Unauthorized();
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var eventType = root.TryGetProperty("event", out var evtProp) ? evtProp.GetString() : null;
            if (eventType != "charge.success") return Ok();

            var data = root.GetProperty("data");
            var reference = data.TryGetProperty("reference", out var refProp) ? refProp.GetString() : null;

            // Only handle our subscription charges (metadata.purpose stamped at initiation).
            string? purpose = null, plan = null;
            if (data.TryGetProperty("metadata", out var meta) && meta.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (meta.TryGetProperty("purpose", out var p)) purpose = p.GetString();
                if (meta.TryGetProperty("plan", out var pl)) plan = pl.GetString();
            }
            if (purpose != "api_connector_subscription" || string.IsNullOrEmpty(reference))
                return Ok(); // not ours / nothing to do — ack so Paystack stops retrying

            // Re-verify against the API before activating (defence in depth), then activate idempotently.
            var (ok, paid, _) = await _subPay.VerifyAsync(reference);
            if (ok && paid)
            {
                var renews = await _subPay.ActivateAsync(plan ?? "monthly");
                try { await _audit.LogAsync("Update", "Subscription", null, $"Subscription activated via webhook ({reference}); renews {renews}", performedBy: "API System"); } catch { }
                _logger.LogInformation("Subscription activated via webhook: {Reference}", reference);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Subscription webhook processing failed");
            // Still ack — a 500 would make Paystack retry indefinitely; the browser callback is the backup.
        }
        return Ok();
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

            // Resolve the order precisely — no loose substring matching (a short OrderNumber could
            // otherwise be a substring of an unrelated reference). We stamp order_number into the
            // transaction metadata at initiation, so match that exactly; fall back to the exact
            // stored PaymentReference (set once the browser callback has run).
            string? orderNumber = null;
            if (data.TryGetProperty("metadata", out var meta)
                && meta.ValueKind == System.Text.Json.JsonValueKind.Object
                && meta.TryGetProperty("order_number", out var onProp))
                orderNumber = onProp.GetString();

            Order? order = null;
            if (!string.IsNullOrEmpty(orderNumber))
                order = await _db.Orders.FirstOrDefaultAsync(o => o.OrderNumber == orderNumber);
            if (order == null && !string.IsNullOrEmpty(reference))
                order = await _db.Orders.FirstOrDefaultAsync(o => o.PaymentReference == reference);

            if (order != null && !order.IsPaid && !string.IsNullOrEmpty(reference))
            {
                // Confirm + fulfil through the shared capture path (idempotent — guards on IsPaid, so
                // the browser callback and this webhook can both fire for the same payment and only the
                // first does the work). It also sends the customer confirmation + admin new-order alert,
                // so an order paid when the shopper never returned to the site is still notified.
                await _capture.CaptureAsync(order.Id, reference, amount, "Paystack");
            }
        }

        return Ok();
    }

    /// <summary>
    /// Inbound from Sterlin Glams Logistics: a driver marked a delivery complete. Verifies the
    /// HMAC-SHA256 signature (x-sg-signature, shared secret), then marks the matching order
    /// Delivered. Idempotent — re-delivery of the same event is a no-op. The logistics app already
    /// notifies the customer, so we don't re-email here.
    /// </summary>
    [HttpPost("logistics/delivered")]
    public async Task<IActionResult> LogisticsDelivered()
    {
        if (!_logistics.IsConfigured)
            return StatusCode(503, new { ok = false, error = "Logistics integration not configured." });

        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var raw = await reader.ReadToEndAsync();

        var sig = Request.Headers["x-sg-signature"].FirstOrDefault();
        if (string.IsNullOrEmpty(sig) || !FixedTimeEquals(sig, _logistics.ComputeSignature(raw)))
        {
            _logger.LogWarning("Rejected logistics delivered callback: bad signature.");
            return Unauthorized(new { ok = false, error = "Invalid signature." });
        }

        string? orderNumber = null;
        string? signerName = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("orderNumber", out var on)) orderNumber = on.GetString();
            if (root.TryGetProperty("signerName", out var sn)) signerName = sn.GetString();
        }
        catch { return BadRequest(new { ok = false, error = "Invalid JSON." }); }

        if (string.IsNullOrWhiteSpace(orderNumber))
            return BadRequest(new { ok = false, error = "orderNumber is required." });

        var order = await _db.Orders.FirstOrDefaultAsync(o => o.OrderNumber == orderNumber);
        if (order == null)
        {
            // Not one of ours (e.g. a legacy WooCommerce order) — ack so logistics doesn't retry.
            _logger.LogInformation("Logistics delivered callback for unknown order {OrderNumber} — ignored.", orderNumber);
            return Ok(new { ok = true, matched = false });
        }

        // Idempotent + don't override a terminal state.
        if (order.Status is OrderStatus.Delivered or OrderStatus.Cancelled or OrderStatus.Refunded)
            return Ok(new { ok = true, alreadyFinal = true, status = order.Status.ToString() });

        var old = order.Status;
        order.Status = OrderStatus.Delivered;
        order.UpdatedAt = DateTime.UtcNow;
        var note = $"Marked Delivered via Sterlin Glams Logistics{(string.IsNullOrWhiteSpace(signerName) ? "" : $" (signed by {signerName})")}.";
        SterlingLams.Web.Services.OrderNotes.AddSystem(_db, order.Id, note);
        await _db.SaveChangesAsync();
        try { await _audit.LogAsync("Delivery", "Order", order.Id.ToString(), $"Order {order.OrderNumber} delivered (logistics): {old} → Delivered"); } catch { }

        _logger.LogInformation("Order {OrderNumber} marked Delivered via logistics callback.", order.OrderNumber);
        return Ok(new { ok = true, matched = true });
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}
