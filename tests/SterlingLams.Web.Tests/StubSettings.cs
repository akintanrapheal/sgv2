using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Tests;

/// <summary>
/// Minimal <see cref="ISettingsService"/> for unit tests: returns the caller's default for every
/// key (optionally overriding specific keys). No DB, no cache.
/// </summary>
public sealed class StubSettings : ISettingsService
{
    private readonly Dictionary<string, string> _overrides;
    public StubSettings(Dictionary<string, string>? overrides = null) => _overrides = overrides ?? new();

    public Task<string> GetAsync(string key, string defaultValue = "")
        => Task.FromResult(_overrides.TryGetValue(key, out var v) ? v : defaultValue);
    public Task<bool> GetBoolAsync(string key, bool defaultValue = false)
        => Task.FromResult(_overrides.TryGetValue(key, out var v) ? bool.Parse(v) : defaultValue);
    public Task<decimal> GetDecimalAsync(string key, decimal defaultValue = 0)
        => Task.FromResult(_overrides.TryGetValue(key, out var v) ? decimal.Parse(v) : defaultValue);
    public Task<int> GetIntAsync(string key, int defaultValue = 0)
        => Task.FromResult(_overrides.TryGetValue(key, out var v) ? int.Parse(v) : defaultValue);
    public Task SaveManyAsync(Dictionary<string, string> values) => Task.CompletedTask;
    public Task<List<SiteSetting>> GetGroupAsync(string group) => Task.FromResult(new List<SiteSetting>());
    public Task<List<SiteSetting>> GetAllAsync() => Task.FromResult(new List<SiteSetting>());
    public void ClearCache() { }
}
