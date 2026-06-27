namespace SterlingLams.Web.Services.Logistics;

/// <summary>
/// Config for the Sterlin Glams Logistics (Lagos delivery) integration — bound from the
/// "Logistics" section. Inert until <see cref="Enabled"/> + <see cref="SharedSecret"/> are set
/// (set them as Render env vars: Logistics__Enabled, Logistics__SharedSecret, Logistics__PushUrl).
/// The same <see cref="SharedSecret"/> signs the outbound order push and verifies the inbound
/// "delivered" callback (HMAC-SHA256, base64).
/// </summary>
public class LogisticsOptions
{
    public bool Enabled { get; set; }
    /// <summary>Logistics endpoint that receives a new delivery order (our → logistics).</summary>
    public string PushUrl { get; set; } = "https://sterlinglamslogistics.com/api/external-orders";
    public string SharedSecret { get; set; } = string.Empty;
    /// <summary>States this courier serves — only delivery orders whose state matches are pushed.
    /// Matched case-insensitively, tolerating a trailing " State" (e.g. "Lagos State" = "Lagos").
    /// Empty = no state filter (push every delivery order).</summary>
    public List<string> DeliveryStates { get; set; } = new() { "Lagos" };
    /// <summary>Display name + pickup address sent with each pushed order.</summary>
    public string PickupName { get; set; } = "Sterlin Glams";
    public string PickupAddress { get; set; } = "Sterlin Glams – Ikota Ajah Lagos";
    public string PickupPhone { get; set; } = "+234 9160009893";
}
