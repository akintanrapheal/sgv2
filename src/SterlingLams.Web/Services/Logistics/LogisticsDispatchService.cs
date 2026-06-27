using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services.Logistics;

public interface ILogisticsDispatchService
{
    /// <summary>Pushes a paid online DELIVERY order to the logistics dashboard, once. Guarded +
    /// idempotent (Order.LogisticsPushedAt) and best-effort — never throws into the caller.</summary>
    Task PushOrderAsync(int orderId);

    /// <summary>HMAC-SHA256 (base64) of the body using the shared secret — used to verify the
    /// inbound "delivered" callback signature.</summary>
    string ComputeSignature(string body);

    bool IsConfigured { get; }
}

public class LogisticsDispatchService : ILogisticsDispatchService
{
    private readonly ApplicationDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly LogisticsOptions _opt;
    private readonly ILogger<LogisticsDispatchService> _log;

    public LogisticsDispatchService(ApplicationDbContext db, IHttpClientFactory http,
        IOptions<LogisticsOptions> opt, ILogger<LogisticsDispatchService> log)
    {
        _db = db;
        _http = http;
        _opt = opt.Value;
        _log = log;
    }

    public bool IsConfigured => _opt.Enabled && !string.IsNullOrWhiteSpace(_opt.SharedSecret);

    public string ComputeSignature(string body)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(_opt.SharedSecret));
        return Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(body)));
    }

    public async Task PushOrderAsync(int orderId)
    {
        if (!IsConfigured) return;
        try
        {
            var order = await _db.Orders
                .Include(o => o.Items)
                .Include(o => o.DeliveryAddress)
                .Include(o => o.User)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order is null) return;
            // Only paid, online, delivery orders that are ready to dispatch — and never twice.
            if (order.LogisticsPushedAt != null) return;
            if (!order.IsPaid || order.Channel != OrderChannel.Online) return;
            if (order.FulfillmentType != FulfillmentType.Delivery) return;
            if (order.Status is not (OrderStatus.Confirmed or OrderStatus.Processing or OrderStatus.Shipped)) return;

            var a = order.DeliveryAddress;
            var address = a == null ? "" : string.Join(", ", new[]
                { a.Line1, a.Line2, a.City, a.State, a.Country }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (string.IsNullOrWhiteSpace(address))
            {
                _log.LogWarning("Logistics push skipped for {OrderNumber}: no delivery address.", order.OrderNumber);
                return;
            }

            var payload = new
            {
                orderNumber = order.OrderNumber,
                customerName = a?.FullName ?? order.User?.FullName ?? "Customer",
                phone = a?.Phone ?? order.User?.PhoneNumber ?? "",
                customerEmail = order.User?.Email,
                address,
                items = order.Items.Select(i => new
                {
                    name = string.IsNullOrEmpty(i.VariantName) ? i.ProductName : $"{i.ProductName} ({i.VariantName})",
                    qty = i.Quantity,
                    price = i.UnitPrice
                }),
                subtotal = order.Subtotal,
                deliveryFees = order.DeliveryFee,
                discount = order.DiscountAmount,
                amount = order.Total,
                deliveryInstruction = order.Notes ?? "",
                paymentMethod = order.PaymentProvider ?? "Paid online",
                pickupName = _opt.PickupName,
                pickupPhone = _opt.PickupPhone,
                pickupAddress = _opt.PickupAddress,
            };

            var body = JsonSerializer.Serialize(payload);
            var req = new HttpRequestMessage(HttpMethod.Post, _opt.PushUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.Add("x-sg-signature", ComputeSignature(body));

            var client = _http.CreateClient("logistics");
            var res = await client.SendAsync(req);
            if (res.IsSuccessStatusCode)
            {
                order.LogisticsPushedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                SterlingLams.Web.Services.OrderNotes.AddSystem(_db, order.Id, "Sent to Sterlin Glams Logistics for delivery.");
                await _db.SaveChangesAsync();
                _log.LogInformation("Pushed order {OrderNumber} to logistics.", order.OrderNumber);
            }
            else
            {
                _log.LogWarning("Logistics push for {OrderNumber} failed: {Status}", order.OrderNumber, (int)res.StatusCode);
            }
        }
        catch (Exception ex)
        {
            // Best-effort — the delivery push must never break checkout / status updates.
            _log.LogError(ex, "Logistics push errored for order {OrderId}.", orderId);
        }
    }
}
