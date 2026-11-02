using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

/// <summary>Fires Retainful commerce lifecycle events for an order, resolving its contact details,
/// total and item count in a single query. Only WEBSITE (online) orders are sent — POS/offline sales
/// are never pushed to Retainful. All work is best-effort and never throws, so it can sit on any
/// order path without risk. Uses the order number as Retainful's uniqueIdentifier so every event for
/// one purchase (checkout → paid → fulfilled → cancelled/refunded) is tied together.</summary>
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
    private readonly ILogger<RetainfulOrderNotifier> _log;

    public RetainfulOrderNotifier(ApplicationDbContext db, IRetainfulClient client,
        ISettingsService settings, ILogger<RetainfulOrderNotifier> log)
    {
        _db = db;
        _client = client;
        _settings = settings;
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
                    ItemCount = x.Items.Sum(i => (int?)i.Quantity) ?? 0,
                })
                .FirstOrDefaultAsync();
            if (o == null) return;                 // not found, or not an online order → nothing to send
            if (string.IsNullOrWhiteSpace(o.Email) && string.IsNullOrWhiteSpace(o.Phone)) return;

            foreach (var name in eventNames)
                await _client.SendOrderEventAsync(name, o.OrderNumber, o.Email, o.Phone, o.First, o.Last,
                    o.Total, o.ItemCount, status: name, channel: o.Channel.ToString());
        }
        catch (Exception ex) { _log.LogWarning(ex, "Retainful notify failed for order {OrderId}.", orderId); }
    }
}
