using Microsoft.Extensions.DependencyInjection;

namespace SterlingLams.Web.Services.Marketing;

/// <summary>
/// Sends a Meta Conversions API funnel event (AddToCart, InitiateCheckout) WITHOUT blocking the request.
/// High-frequency storefront actions must not wait on an ad platform, so the caller builds the visitor
/// <see cref="MetaEventContext"/> from HttpContext synchronously, then this fires the Graph call on a
/// background task in its OWN DI scope — the request scope (and its scoped HttpClient) may already be gone
/// by the time Graph responds. Best-effort: a lost event only costs a little attribution, never the action.
/// </summary>
public interface IMetaCapiDispatcher
{
    /// <summary>Queues the event and returns immediately. Does nothing heavy inline.</summary>
    void Dispatch(string eventName, string eventId, MetaEventContext ctx, object customData);
}

public sealed class MetaCapiDispatcher : IMetaCapiDispatcher
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MetaCapiDispatcher> _log;

    public MetaCapiDispatcher(IServiceScopeFactory scopes, ILogger<MetaCapiDispatcher> log)
    {
        _scopes = scopes;
        _log = log;
    }

    public void Dispatch(string eventName, string eventId, MetaEventContext ctx, object customData)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var capi = scope.ServiceProvider.GetRequiredService<IMetaConversionsApi>();
                // Capped so a slow/unreachable Graph API can't pin a background thread indefinitely.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await capi.SendEventAsync(eventName, eventId, ctx, customData, cts.Token);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Meta CAPI dispatch for {Event} failed", eventName);
            }
        });
    }
}
