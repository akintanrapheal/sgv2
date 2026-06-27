using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

public record GiftCardLookup(bool Ok, string Message, decimal Balance, string? Code);

public interface IGiftCardService
{
    /// <summary>Whether gift-card redemption at checkout is enabled.</summary>
    Task<bool> RedemptionEnabledAsync();

    /// <summary>Issues a new card with a unique code and an Issue ledger entry. Returns the saved card.</summary>
    Task<GiftCard> IssueAsync(decimal amount, string? recipientName, string? recipientEmail,
        string? note, DateTime? expiresAt, string? byUserId);

    /// <summary>Validates a code for redemption (active, in-date, balance &gt; 0). Never throws.</summary>
    Task<GiftCardLookup> ValidateAsync(string? code);

    /// <summary>Public balance check — same as Validate but also reports deactivated/expired/spent cards clearly.</summary>
    Task<GiftCardLookup> CheckBalanceAsync(string? code);

    /// <summary>Draws the amount earmarked on a paid order (Order.GiftCardAmount), once.
    /// Safe to call from every paid-order path — idempotent via Order.GiftCardRedeemedAt.</summary>
    Task RedeemForOrderAsync(int orderId);

    /// <summary>Returns the drawn amount to the card on a full refund, once (idempotent via Order.GiftCardReversedAt).</summary>
    Task ReverseForOrderAsync(int orderId);

    /// <summary>Normalises a typed code (uppercase, trims, collapses spaces) for lookup.</summary>
    string Normalize(string? code);
}

public class GiftCardService : IGiftCardService
{
    private readonly ApplicationDbContext _db;
    private readonly ISettingsService _settings;
    private readonly ILogger<GiftCardService> _logger;

    // Unambiguous alphabet (no 0/O, 1/I) for codes that get read aloud / typed by hand.
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public GiftCardService(ApplicationDbContext db, ISettingsService settings, ILogger<GiftCardService> logger)
    {
        _db = db;
        _settings = settings;
        _logger = logger;
    }

    public Task<bool> RedemptionEnabledAsync() => _settings.GetBoolAsync("giftcards.enabled", true);

    public string Normalize(string? code) =>
        string.IsNullOrWhiteSpace(code) ? string.Empty : code.Trim().ToUpperInvariant().Replace(" ", "");

    public async Task<GiftCard> IssueAsync(decimal amount, string? recipientName, string? recipientEmail,
        string? note, DateTime? expiresAt, string? byUserId)
    {
        if (amount <= 0) throw new ArgumentException("Gift card amount must be greater than zero.", nameof(amount));
        amount = Math.Round(amount, 2);

        var now = DateTime.UtcNow;
        var card = new GiftCard
        {
            Code = await GenerateUniqueCodeAsync(),
            InitialAmount = amount,
            Balance = amount,
            IsActive = true,
            ExpiresAt = expiresAt,
            RecipientName = string.IsNullOrWhiteSpace(recipientName) ? null : recipientName.Trim(),
            RecipientEmail = string.IsNullOrWhiteSpace(recipientEmail) ? null : recipientEmail.Trim(),
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            IssuedByUserId = byUserId,
            CreatedAt = now
        };
        card.Transactions.Add(new GiftCardTransaction
        {
            Amount = amount,
            Type = GiftCardTxnType.Issue,
            Note = "Card issued",
            CreatedAt = now
        });
        _db.GiftCards.Add(card);
        await _db.SaveChangesAsync();
        return card;
    }

    public async Task<GiftCardLookup> ValidateAsync(string? code)
    {
        var norm = Normalize(code);
        if (string.IsNullOrEmpty(norm)) return new(false, "Enter a gift card code.", 0, null);

        var card = await _db.GiftCards.AsNoTracking().FirstOrDefaultAsync(g => g.Code == norm);
        if (card is null) return new(false, "That gift card code wasn't found.", 0, null);
        if (!card.IsActive) return new(false, "This gift card is no longer active.", 0, card.Code);
        if (card.IsExpired) return new(false, "This gift card has expired.", 0, card.Code);
        if (card.Balance <= 0) return new(false, "This gift card has no balance left.", 0, card.Code);

        return new(true, $"₦{card.Balance:N0} available.", card.Balance, card.Code);
    }

    public async Task<GiftCardLookup> CheckBalanceAsync(string? code)
    {
        var norm = Normalize(code);
        if (string.IsNullOrEmpty(norm)) return new(false, "Enter a gift card code.", 0, null);

        var card = await _db.GiftCards.AsNoTracking().FirstOrDefaultAsync(g => g.Code == norm);
        if (card is null) return new(false, "That gift card code wasn't found.", 0, null);
        if (!card.IsActive) return new(false, "This gift card has been deactivated.", card.Balance, card.Code);
        if (card.IsExpired) return new(false, $"This gift card expired on {card.ExpiresAt:d MMM yyyy}.", card.Balance, card.Code);
        if (card.Balance <= 0) return new(false, "This gift card has been fully used.", 0, card.Code);

        return new(true, $"Balance: ₦{card.Balance:N0}", card.Balance, card.Code);
    }

    public async Task RedeemForOrderAsync(int orderId)
    {
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
        if (order is null || string.IsNullOrEmpty(order.GiftCardCode) || order.GiftCardAmount <= 0) return;
        if (order.GiftCardRedeemedAt != null) return; // already drawn (idempotent)

        var card = await _db.GiftCards.FirstOrDefaultAsync(g => g.Code == order.GiftCardCode);
        var now = DateTime.UtcNow;
        order.GiftCardRedeemedAt = now;

        if (card != null)
        {
            // Clamp to the balance actually available now (guards a concurrent spend between
            // earmark-at-checkout and draw-at-payment); never go below zero.
            var toDraw = Math.Min(order.GiftCardAmount, card.Balance);
            if (toDraw > 0)
            {
                card.Balance -= toDraw;
                card.LastUsedAt = now;
                card.Transactions.Add(new GiftCardTransaction
                {
                    Amount = -toDraw,
                    Type = GiftCardTxnType.Redeem,
                    OrderId = order.Id,
                    Note = $"Redeemed on order {order.OrderNumber}",
                    CreatedAt = now
                });
                if (toDraw < order.GiftCardAmount)
                    _logger.LogWarning("Order {OrderNumber} earmarked ₦{Earmarked} of gift card {Code} but only ₦{Drawn} was available.",
                        order.OrderNumber, order.GiftCardAmount, card.Code, toDraw);
            }
        }
        else
        {
            _logger.LogWarning("Order {OrderNumber} references gift card {Code} which no longer exists.",
                order.OrderNumber, order.GiftCardCode);
        }

        await _db.SaveChangesAsync();
    }

    public async Task ReverseForOrderAsync(int orderId)
    {
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId);
        if (order is null || order.GiftCardReversedAt != null) return; // idempotent
        // Only return funds that were actually drawn.
        if (order.GiftCardRedeemedAt == null || string.IsNullOrEmpty(order.GiftCardCode) || order.GiftCardAmount <= 0) return;

        var now = DateTime.UtcNow;
        order.GiftCardReversedAt = now;

        var card = await _db.GiftCards.FirstOrDefaultAsync(g => g.Code == order.GiftCardCode);
        if (card != null)
        {
            card.Balance += order.GiftCardAmount;
            card.Transactions.Add(new GiftCardTransaction
            {
                Amount = order.GiftCardAmount,
                Type = GiftCardTxnType.Refund,
                OrderId = order.Id,
                Note = $"Returned on refund of order {order.OrderNumber}",
                CreatedAt = now
            });
        }

        await _db.SaveChangesAsync();
    }

    private async Task<string> GenerateUniqueCodeAsync()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var code = "SLGC-" + RandomBlock(4) + "-" + RandomBlock(4);
            if (!await _db.GiftCards.AnyAsync(g => g.Code == code)) return code;
        }
        // Astronomically unlikely; widen the entropy as a last resort.
        return "SLGC-" + RandomBlock(6) + "-" + RandomBlock(6);
    }

    private static string RandomBlock(int len)
    {
        var sb = new StringBuilder(len);
        Span<byte> buf = stackalloc byte[len];
        RandomNumberGenerator.Fill(buf);
        foreach (var b in buf) sb.Append(CodeAlphabet[b % CodeAlphabet.Length]);
        return sb.ToString();
    }
}
