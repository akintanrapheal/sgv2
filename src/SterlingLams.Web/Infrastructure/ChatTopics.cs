namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// The storefront Help-chat quick-reply topics (and issue sub-topics). The widget shows the buttons and a
/// rich auto-reply; the server stores the shopper's selection + a plain-text auto-reply to the conversation
/// so customer care sees the full journey (which topic/issue, then the shopper's own explanation). Keys are
/// shared between the widget and the <c>/chat/topic</c> endpoint.
/// </summary>
public static class ChatTopics
{
    /// <param name="Alert">true for issue topics (a real problem → ping staff); false for the
    /// self-service info topics (order tracking, delivery, sizing) which only need to be logged.</param>
    public record Topic(string Key, string Label, string AutoReply, bool Alert);

    private static readonly Dictionary<string, Topic> All = new(StringComparer.Ordinal)
    {
        ["track"] = new("track", "Track my order",
            "You can check your order status any time with your order number and email on our Track Order page. " +
            "Or send us your order number below and our team will help you.", false),
        ["returns"] = new("returns", "Delivery & returns",
            "We deliver nationwide with same-day and express options, and unworn items can be returned within our " +
            "policy window. Tell us your order number below if it's about a specific order.", false),
        ["sizing"] = new("sizing", "Sizing help",
            "Happy to help you find the right fit — tell us the piece you're interested in below and we'll check " +
            "the measurements for you.", false),

        ["issue_payment"] = new("issue_payment", "Payment issue",
            "Sorry about the payment trouble. Please describe what happened (and include your order number if you " +
            "have one) below and our team will sort it out.", true),
        ["issue_cart"] = new("issue_cart", "Adding to cart / checkout",
            "Sorry you're having trouble checking out. Tell us the product and what happens when you try, and we'll help.", true),
        ["issue_delivery"] = new("issue_delivery", "Delivery problem",
            "Sorry about the delivery issue. Share your order number and what's wrong below and we'll look into it.", true),
        ["issue_item"] = new("issue_item", "Wrong or damaged item",
            "So sorry your item isn't right. Describe the problem below (you can attach a photo too) and we'll make it right.", true),
        ["issue_account"] = new("issue_account", "Login / account issue",
            "Sorry about the account trouble. Tell us what's happening below and our team will help you.", true),
        ["issue_other"] = new("issue_other", "Something else",
            "Tell us what's going on below and our team will help.", true),
    };

    public static Topic? Get(string? key) => key != null && All.TryGetValue(key, out var t) ? t : null;
}
