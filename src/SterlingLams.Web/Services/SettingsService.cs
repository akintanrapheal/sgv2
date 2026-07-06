using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

public interface ISettingsService
{
    Task<string> GetAsync(string key, string defaultValue = "");
    Task<bool> GetBoolAsync(string key, bool defaultValue = false);
    Task<decimal> GetDecimalAsync(string key, decimal defaultValue = 0);
    Task<int> GetIntAsync(string key, int defaultValue = 0);
    Task SaveManyAsync(Dictionary<string, string> values);
    Task<List<SiteSetting>> GetGroupAsync(string group);
    Task<List<SiteSetting>> GetAllAsync();
    void ClearCache();
}

public class SettingsService : ISettingsService
{
    private readonly ApplicationDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ISettingsSecretProtector _secrets;
    private const string CacheKey = "site_settings_all";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    public SettingsService(ApplicationDbContext db, IMemoryCache cache, ISettingsSecretProtector secrets)
    {
        _db = db;
        _cache = cache;
        _secrets = secrets;
    }

    public async Task<string> GetAsync(string key, string defaultValue = "")
    {
        var all = await LoadAllAsync();
        return all.TryGetValue(key, out var v) ? v : defaultValue;
    }

    public async Task<bool> GetBoolAsync(string key, bool defaultValue = false)
    {
        var val = await GetAsync(key);
        if (string.IsNullOrEmpty(val)) return defaultValue;
        return val == "true" || val == "1" || val.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<decimal> GetDecimalAsync(string key, decimal defaultValue = 0)
    {
        var val = await GetAsync(key);
        return decimal.TryParse(val, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : defaultValue;
    }

    public async Task<int> GetIntAsync(string key, int defaultValue = 0)
    {
        var val = await GetAsync(key);
        return int.TryParse(val, out var i) ? i : defaultValue;
    }

    public async Task SaveManyAsync(Dictionary<string, string> values)
    {
        var keys = values.Keys.ToList();
        var existing = await _db.SiteSettings
            .Where(s => keys.Contains(s.Key))
            .ToListAsync();

        foreach (var kv in values)
        {
            var setting = existing.FirstOrDefault(s => s.Key == kv.Key);
            if (setting != null)
                setting.Value = kv.Value ?? string.Empty;
        }

        await _db.SaveChangesAsync();
        ClearCache();
    }

    public async Task<List<SiteSetting>> GetGroupAsync(string group) =>
        await _db.SiteSettings
            .Where(s => s.Group == group)
            .OrderBy(s => s.SortOrder)
            .ToListAsync();

    public async Task<List<SiteSetting>> GetAllAsync() =>
        await _db.SiteSettings.OrderBy(s => s.Group).ThenBy(s => s.SortOrder).ToListAsync();

    public void ClearCache() => _cache.Remove(CacheKey);

    private async Task<Dictionary<string, string>> LoadAllAsync()
    {
        if (_cache.TryGetValue(CacheKey, out Dictionary<string, string>? cached) && cached != null)
            return cached;

        var settings = await _db.SiteSettings.ToListAsync();
        // Reveal (decrypt) any secret values stored with the enc: sentinel, so all runtime reads
        // (payment keys, SMTP password) get plaintext. Non-secret values pass through unchanged.
        var dict = settings.ToDictionary(s => s.Key, s => _secrets.Reveal(s.Value));
        _cache.Set(CacheKey, dict, CacheTtl);
        return dict;
    }
}
