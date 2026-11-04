using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

/// <summary>Fires Retainful commerce lifecycle events for an order, resolving its contact details,
/// total, line items and (for CHECKOUT_CREATED) a recovery URL in a single query. Only WEBSITE (online)
/// orders are sent — POS/offline sales are never pushed to Retainful. All work is best-effort and never
/// throws, so it can sit on any order path without risk. Uses the order number as Retainful's
/// uniqueIdentifier so every event for one purchase (checkout → paid → fulfilled → cancelled/refunded)
/// is tied together.</summary>
public interface IRetainfulOrderNotifier
{
    /// <summary>Send one or more events (e.g. "CHECKOUT_CREATED", "ORDER_PLACED") for this order.</summary>
    Task NotifyAsync(int orderId, params string[] eventNames);
}

public class RetainfulOrderNotifier : IRetainfulOrderNotifier
{
    private readonly ApplicationDbContext _db;
    private readonly IRetainfulClient _client;
    private readonly ISettingsService _settings;
    private readonly IConfiguration _config;
    private readonly IDataProtectionProvider _dp;
    private readonly ILogger<RetainfulOrderNotifier> _log;

    public RetainfulOrderNotifier(ApplicationDbContext db, IRetainfulClient client,
        ISettingsService settings, IConfiguration config, IDataProtectionProvider dp,
        ILogger<RetainfulOrderNotifier> log)
    {
        _db = db;
        _client = client;
        _settings = settings;
        _config = config;
        _dp = dp;
        _log = log;
    }

    public async Task NotifyAsync(int orderId, params string[] eventNames)
    {
        if (eventNames == null || eventNames.Length == 0) return;
        try
        {
            // Skip the DB read entirely when Retainful is off.
            if (!await _settings.GetBoolAsync("retainful.enabled", false)) return;

            var o = await _db.Orders.AsNoTracking()
                .Where(x => x.Id == orderId && x.Channel == OrderChannel.Online)
                .Select(x => new
                {
                    x.OrderNumber,
                    x.Total,
                    x.Channel,
                    Email = x.User.Email,
                    Phone = x.ContactPhone ?? x.User.PhoneNumber,
                    First = x.User.FirstName,
                    Last = x.User.LastName,
                    Items = x.Items.Select(i => new
                    {
                        i.ProductId,
                        i.ProductName,
                        i.VariantName,
                        i.Quantity,
                        i.UnitPrice,
                        Slug = i.Product.Slug,
                        Image = i.Product.Images
                            .OrderByDescending(im => im.IsPrimary).ThenBy(im => im.SortOrder)
                            .Select(im => im.Url).FirstOrDefault(),
                    }).ToList(),
                })
                .FirstOrDefaultAsync();
            if (o == null) return;                 // not found, or not an online order → nothing to send
            if (string.IsNullOrWhiteSpace(o.Email) && string.IsNullOrWhiteSpace(o.Phone)) return;

            var baseUrl = (_config["App:BaseUrl"] ?? "").TrimEnd('/');
            string Abs(string? u) => string.IsNullOrEmpty(u) ? "" : (u.StartsWith("/") ? baseUrl + u : u);

            // Line items in Retainful's shape (name shows the variant, price is the unit price).
            var lines = o.Items.Select(i => new RetainfulLineItem(
                Name: i.ProductName + (string.IsNullOrEmpty(i.VariantName) ? "" : $" – {i.VariantName}"),
                Price: i.UnitPrice,
                Quantity: i.Quantity,
                ImageUrl: Abs(i.Image),
                ProductId: i.ProductId.ToString(),
                ProductUrl: baseUrl.Length > 0 && !string.IsNullOrEmpty(i.Slug) ? $"{baseUrl}/products/{i.Slug}" : ""
            )).ToList();
            var itemCount = o.Items.Sum(i => i.Quantity);

            // The recovery link (CHECKOUT_CREATED only) brings an abandoned shopper back to a rebuilt
            // checkout. Needs an absolute base URL; without one we simply omit it.
            string? recoveryUrl = null;
            if (baseUrl.Length > 0)
            {
                var token = CheckoutRecoveryToken.Create(_dp, o.OrderNumber);
                recoveryUrl = $"{baseUrl}/checkout/recover?t={Uri.EscapeDataString(token)}";
            }

            foreach (var name in eventNames)
            {
                var recovery = string.Equals(name, "CHECKOUT_CREATED", StringComparison.OrdinalIgnoreCase)
                    ? recoveryUrl : null;
                await _client.SendOrderEventAsync(name, o.OrderNumber, o.Email, o.Phone, o.First, o.Last,
                    o.Total, itemCount, status: name, channel: o.Channel.ToString(),
                    lines: lines, recoveryUrl: recovery, currency: "NGN");
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Retainful notify failed for order {OrderId}.", orderId); }
    }
}
