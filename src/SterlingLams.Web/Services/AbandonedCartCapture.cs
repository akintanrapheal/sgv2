using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Models.ViewModels;

namespace SterlingLams.Web.Services;

/// <summary>
/// Upserts the one-row-per-email abandoned-cart snapshot used by <see cref="Infrastructure.AbandonedCartService"/>
/// to send recovery emails. Captured both when a signed-in shopper changes their bag (add-to-cart
/// abandonment) and at checkout (checkout abandonment) — same row, refreshed each time.
/// </summary>
public interface IAbandonedCartCapture
{
    /// <summary>Save/refresh the snapshot for this email and reset the recovery clock. No-op on a
    /// blank email or empty cart.</summary>
    Task CaptureAsync(string? email, CartViewModel cart);

    /// <summary>Mark this email's snapshot recovered (so no recovery email is sent) — e.g. when the
    /// shopper empties their bag. No-op if there's no open snapshot.</summary>
    Task MarkRecoveredAsync(string? email);
}

public class AbandonedCartCapture : IAbandonedCartCapture
{
    private readonly ApplicationDbContext _db;
    public AbandonedCartCapture(ApplicationDbContext db) => _db = db;

    public async Task CaptureAsync(string? email, CartViewModel cart)
    {
        if (string.IsNullOrWhiteSpace(email) || cart.IsEmpty) return;
        email = email.Trim();
        var snapshot = JsonSerializer.Serialize(
            cart.Items.Select(i => new { i.ProductId, i.VariantId, i.Quantity }));
        var now = DateTime.UtcNow;

        var existing = await _db.AbandonedCarts.FirstOrDefaultAsync(a => a.Email == email);
        if (existing == null)
        {
            _db.AbandonedCarts.Add(new AbandonedCart
            {
                Email = email,
                Token = Guid.NewGuid().ToString("N"),
                ItemsJson = snapshot,
                Subtotal = cart.Subtotal,
                ItemCount = cart.TotalItems,
                CreatedAt = now
            });
        }
        else
        {
            existing.Token = Guid.NewGuid().ToString("N");
            existing.ItemsJson = snapshot;
            existing.Subtotal = cart.Subtotal;
            existing.ItemCount = cart.TotalItems;
            existing.CreatedAt = now;      // reset the clock so the email only fires once they go quiet
            existing.EmailedAt = null;
            existing.RecoveredAt = null;
        }
        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateException) { _db.ChangeTracker.Clear(); } // benign race on the unique email
    }

    public async Task MarkRecoveredAsync(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return;
        email = email.Trim();
        var existing = await _db.AbandonedCarts.FirstOrDefaultAsync(a => a.Email == email && a.RecoveredAt == null);
        if (existing == null) return;
        existing.RecoveredAt = DateTime.UtcNow;
        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateException) { _db.ChangeTracker.Clear(); }
    }
}
