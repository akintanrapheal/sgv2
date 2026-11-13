namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// One message in a storefront live-chat thread (<see cref="ChatConversation"/>). Shoppers send from the
/// Help widget (storefront ChatController); staff reply from the admin inbox (ChatMessagesController).
/// Polling delivers each side's new messages to the other. Every message is timestamped (<see cref="CreatedAt"/>).
/// </summary>
public class ChatMessage
{
    public int Id { get; set; }

    public int ConversationId { get; set; }
    public ChatConversation Conversation { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>true = sent by a staff member from the admin; false = sent by the shopper.</summary>
    public bool FromStaff { get; set; }

    public string Body { get; set; } = string.Empty;

    /// <summary>Attached image URL (raw R2 object) when the message is a shared photo. Stored as the
    /// uploaded original — never run through ImageSharp — so the decoder advisories can't be triggered.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Display name for staff messages (the agent's first name). Null for shopper messages.</summary>
    public string? SenderName { get; set; }

    /// <summary>Staff messages: delivered to the shopper's widget (via poll)? Shopper messages: always true.</summary>
    public bool SeenByCustomer { get; set; }
}
