using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Controllers;

/// <summary>
/// Same-origin reverse proxy for PostHog. The storefront runs a strict CSP (script-src / connect-src
/// 'self'), so the browser must never talk to a PostHog host directly. Instead the snippet points at
/// <c>/ingest</c> on our own domain and this forwards to PostHog's regional hosts — which also means
/// ad-blockers don't strip the analytics:
///   <c>/ingest/static/*</c> → <c>{region}-assets.i.posthog.com</c> (the JS bundle + session recorder)
///   <c>/ingest/*</c>        → <c>{region}.i.posthog.com</c>        (events, session replay, flags)
/// Only these two fixed PostHog hosts are ever contacted — it is not an open proxy. Bytes pass through
/// untouched (decompression is off on the client), so this adds no parsing overhead.
/// </summary>
[Route("ingest")]
public class PostHogProxyController : Controller
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ISettingsService _settings;
    private readonly IMemoryCache _cache;

    public PostHogProxyController(IHttpClientFactory httpFactory, ISettingsService settings, IMemoryCache cache)
    {
        _httpFactory = httpFactory;
        _settings = settings;
        _cache = cache;
    }

    // Connection-level headers that must not be copied between the two hops.
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Transfer-Encoding", "Keep-Alive",
        "Upgrade", "Proxy-Connection", "TE", "Trailer",
    };

    [AcceptVerbs("GET", "POST", "OPTIONS")]
    [Route("{**path}")]
    public async Task Proxy(string? path)
    {
        var region = await RegionAsync();
        var isStatic = (path ?? "").StartsWith("static/", StringComparison.OrdinalIgnoreCase);
        var host = isStatic
            ? $"https://{region}-assets.i.posthog.com"
            : $"https://{region}.i.posthog.com";
        var target = $"{host}/{path}{Request.QueryString.Value}";

        var client = _httpFactory.CreateClient("posthog");
        using var req = new HttpRequestMessage(new HttpMethod(Request.Method), target);

        // Forward the request body for verbs that carry one (capture/batch/session-replay POSTs).
        if (Request.ContentLength is > 0 || Request.Headers.ContainsKey("Transfer-Encoding"))
            req.Content = new StreamContent(Request.Body);

        // Copy headers, routing content headers (Content-Type/Length) onto the content object.
        foreach (var h in Request.Headers)
        {
            if (HopByHop.Contains(h.Key)) continue;
            if (!req.Headers.TryAddWithoutValidation(h.Key, (IEnumerable<string>)h.Value) && req.Content != null)
                req.Content.Headers.TryAddWithoutValidation(h.Key, (IEnumerable<string>)h.Value);
        }

        HttpResponseMessage resp;
        try
        {
            resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted);
        }
        catch (OperationCanceledException) { return; }   // client went away; nothing to do
        catch (HttpRequestException) { Response.StatusCode = StatusCodes.Status502BadGateway; return; }

        using (resp)
        {
            Response.StatusCode = (int)resp.StatusCode;
            foreach (var h in resp.Headers)
                if (!HopByHop.Contains(h.Key)) Response.Headers[h.Key] = h.Value.ToArray();
            foreach (var h in resp.Content.Headers)
                if (!HopByHop.Contains(h.Key)) Response.Headers[h.Key] = h.Value.ToArray();
            // Kestrel decides the response framing itself; a copied Transfer-Encoding would double it up.
            Response.Headers.Remove("Transfer-Encoding");

            try
            {
                await resp.Content.CopyToAsync(Response.Body, HttpContext.RequestAborted);
            }
            catch (OperationCanceledException) { /* client disconnected mid-stream */ }
        }
    }

    /// <summary>Data region ("eu" default, or "us"), cached briefly so the hot proxy path doesn't read
    /// settings on every event.</summary>
    private async Task<string> RegionAsync()
    {
        if (_cache.TryGetValue("posthog:region", out string? cached) && cached != null) return cached;
        var region = (await _settings.GetAsync("posthog.region", "eu")).Trim().ToLowerInvariant();
        if (region != "us") region = "eu";
        _cache.Set("posthog:region", region, TimeSpan.FromMinutes(10));
        return region;
    }
}
