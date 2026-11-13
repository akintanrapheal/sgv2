using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Controllers;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Ends live-chat conversations that have gone quiet — no message from the shopper for
/// <see cref="ChatController.TimeoutMinutes"/> (30) minutes. The widget also ends a chat lazily when the
/// shopper is still on the page, but this covers the common case where they simply left: so the admin
/// inbox reflects the ended state and the chat can't sit "open" forever.
/// </summary>
public class ChatTimeoutService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly IServiceProvider _sp;
    private readonly ILogger<ChatTimeoutService> _log;

    public ChatTimeoutService(IServiceProvider sp, ILogger<ChatTimeoutService> log)
    {
        _sp = sp; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "Chat timeout sweep failed."); }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var cutoff = DateTime.UtcNow.AddMinutes(-ChatController.TimeoutMinutes);
        var ended = await db.ChatConversations
            .Where(c => !c.Ended && c.LastCustomerAt < cutoff)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Ended, true)
                .SetProperty(c => c.EndedAt, DateTime.UtcNow)
                .SetProperty(c => c.EndedReason, "timeout"), ct);

        if (ended > 0) _log.LogInformation("Auto-ended {Count} idle chat conversation(s).", ended);
    }
}
