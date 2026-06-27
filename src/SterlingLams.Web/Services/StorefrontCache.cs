using Microsoft.AspNetCore.OutputCaching;

namespace SterlingLams.Web.Services;

/// <summary>
/// Evicts the output-cached storefront pages (home, category lists) so admin edits to products,
/// prices, visibility or homepage/announcement settings show up immediately instead of waiting
/// out the cache TTL. Best-effort — never throws into the calling action.
/// </summary>
public interface IStorefrontCache
{
    Task EvictAsync(CancellationToken ct = default);
}

public class StorefrontCache : IStorefrontCache
{
    public const string Tag = "storefront";

    private readonly IOutputCacheStore _store;
    private readonly ILogger<StorefrontCache> _logger;

    public StorefrontCache(IOutputCacheStore store, ILogger<StorefrontCache> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task EvictAsync(CancellationToken ct = default)
    {
        try { await _store.EvictByTagAsync(Tag, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to evict storefront output cache."); }
    }
}
