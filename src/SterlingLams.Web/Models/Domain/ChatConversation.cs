namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// One storefront live-chat thread. A shopper chats without signing in — a durable cookie key (sg_chat)
/// identifies the conversation, so it survives reloads. Staff reply from Admin → Chat Messages. A chat
/// auto-ends after 30 minutes with no reply from the shopper (<see cref="LastCustomerAt"/>), after which
/// the widget invites a short star review. Messages live in <see cref="ChatMessage"/>.
/// </summary>
public class ChatConversation
{
    public int Id { get; set; }

    /// <summary>The shopper's durable chat cookie value — unique per live thread.</summary>
    public string Key { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastMessageAt { get; set; } = DateTime.UtcNow;
    /// <summary>When the shopper last sent a message — drives the 30-minute inactivity auto-end.</summary>
    public DateTime LastCustomerAt { get; set; } = DateTime.UtcNow;

    // Lifecycle.
    public bool Ended { get; set; }
    public DateTime? EndedAt { get; set; }
    /// <summary>"timeout" (30-min silence) or "staff" (closed from the admin).</summary>
    public string? EndedReason { get; set; }

    // Post-chat review.
    public int? Rating { get; set; }            // 1–5 stars
    public string? ReviewComment { get; set; }
    public DateTime? ReviewedAt { get; set; }

    /// <summary>Shopper messages not yet seen by staff — the admin inbox unread badge.</summary>
    public int StaffUnread { get; set; }

    // Shopper identity (latest known) + first-seen context.
    public string? UserId { get; set; }
    public string? CustomerName { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? FirstPath { get; set; }
    public string? DeviceType { get; set; }
    public string? IpAddress { get; set; }

    // Denormalised last-message preview for the inbox list (avoids a per-row lookup).
    public string? LastBody { get; set; }
    public bool LastFromStaff { get; set; }

    public List<ChatMessage> Messages { get; set; } = new();
}
