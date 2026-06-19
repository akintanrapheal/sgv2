using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

/// <summary>
/// Helper for appending system (auto) notes to an order's timeline. The note is added to the
/// change tracker; the caller saves (so it commits with the surrounding work). Staff/customer
/// notes are created directly in the admin controller (which also emails customer notes).
/// </summary>
public static class OrderNotes
{
    public static void AddSystem(ApplicationDbContext db, int orderId, string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        db.OrderNotes.Add(new OrderNote
        {
            OrderId = orderId,
            Content = content.Trim(),
            IsSystem = true,
            CreatedAt = DateTime.UtcNow
        });
    }
}
