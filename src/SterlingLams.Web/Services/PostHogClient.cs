using System.Text;
using System.Text.Json;

namespace SterlingLams.Web.Services;

/// <summary>
/// Server-side PostHog capture for AUTHORITATIVE events the browser can't be trusted for — chiefly
/// "order_paid" (fired when payment actually clears, even if the shopper closed the tab) and "refund".
/// Best-effort telemetry: settings-gated, self-contained, and NEVER throws — a slow, off, or
/// unconfigured PostHog can't affect checkout or refunds. Sent directly to PostHog's regional host
/// (no CSP applies server-side), unlike the browser which goes through the /ingest proxy.
/// </summary>
public interface IPostHogClient
{
    /// <summary>Records one event against a person. <paramref name="distinctId"/> should match the
    /// browser's PostHog id (we identify signed-in shoppers by their user id) so the funnel links;
    /// for guests pass a stable fallback. Never throws.</summary>
    Task CaptureAsync(string distinctId, string @event, object? properties = null, CancellationToken ct = default);
}

public class PostHogClient : IPostHogClient
{
    private readonly HttpClient _http;
    private readonly ISettingsService _settings;
    private readonly ILogger<PostHogClient> _log;

    public PostHogClient(HttpClient http, ISettingsService settings, ILogger<PostHogClient> log)
    {
        _http = http;
        _settings = settings;
        _log = log;
    }

    public async Task CaptureAsync(string distinctId, string @event, object? properties = null, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(distinctId)) return;
            if (!await _settings.GetBoolAsync("posthog.enabled", false)) return;   // master switch — nothing sent when off
            var key = (await _settings.GetAsync("posthog.project_api_key")).Trim();
            if (key.Length == 0) return;
            var region = (await _settings.GetAsync("posthog.region", "eu")).Trim().ToLowerInvariant();
            if (region != "us") region = "eu";

            var payload = JsonSerializer.Serialize(new
            {
                api_key = key,
                @event = @event,
                distinct_id = distinctId,
                properties = properties ?? new { },
                timestamp = DateTime.UtcNow.ToString("o"),
            });
            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://{region}.i.posthog.com/capture/");
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct);   // best-effort; response intentionally ignored
        }
        catch (Exception ex)
        {
            // Analytics must never break the operation that triggered it.
            _log.LogDebug(ex, "PostHog capture skipped ({Event})", @event);
        }
    }
}
