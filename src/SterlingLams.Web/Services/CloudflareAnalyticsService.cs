using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SterlingLams.Web.Services;

/// <summary>Edge/security analytics pulled from Cloudflare's GraphQL Analytics API (zone-level:
/// requests, threats blocked, unique visitors, top countries). Reads a scoped API token + zone id from
/// settings (Reveal-decrypted) with env-config fallback. Everything degrades gracefully: not configured
/// → <see cref="CloudflareResult.Configured"/> false; any API/parse error → a friendly Error, never a
/// throw into the request.</summary>
public interface ICloudflareAnalytics
{
    Task<bool> IsConfiguredAsync();
    Task<CloudflareResult> GetAsync(int days);
}

public class CloudflareStats
{
    public long Requests { get; set; }
    public long Threats { get; set; }
    public long PageViews { get; set; }
    public long Visitors { get; set; }
    public List<string> DayLabels { get; } = new();
    public List<long> DayRequests { get; } = new();
    public List<long> DayThreats { get; } = new();
    public List<(string Country, long Requests)> TopCountries { get; } = new();
}

public class CloudflareResult
{
    public bool Configured { get; set; }
    public string? Error { get; set; }
    public CloudflareStats? Stats { get; set; }
}

public class CloudflareAnalyticsService : ICloudflareAnalytics
{
    private const string Endpoint = "https://api.cloudflare.com/client/v4/graphql";
    private readonly HttpClient _http;
    private readonly ISettingsService _settings;
    private readonly ISettingsSecretProtector _secrets;
    private readonly IConfiguration _config;
    private readonly ILogger<CloudflareAnalyticsService> _log;

    public CloudflareAnalyticsService(HttpClient http, ISettingsService settings,
        ISettingsSecretProtector secrets, IConfiguration config, ILogger<CloudflareAnalyticsService> log)
    {
        _http = http;
        _settings = settings;
        _secrets = secrets;
        _config = config;
        _log = log;
    }

    private async Task<(string Token, string Zone)> CredsAsync()
    {
        var token = _secrets.Reveal(await _settings.GetAsync("cloudflare.api_token", ""));
        if (string.IsNullOrWhiteSpace(token)) token = _config["Cloudflare:ApiToken"] ?? "";
        var zone = await _settings.GetAsync("cloudflare.zone_id", "");
        if (string.IsNullOrWhiteSpace(zone)) zone = _config["Cloudflare:ZoneId"] ?? "";
        return (token.Trim(), zone.Trim());
    }

    public async Task<bool> IsConfiguredAsync()
    {
        var (t, z) = await CredsAsync();
        return t.Length > 0 && z.Length > 0;
    }

    public async Task<CloudflareResult> GetAsync(int days)
    {
        var (token, zone) = await CredsAsync();
        if (token.Length == 0 || zone.Length == 0)
            return new CloudflareResult { Configured = false };

        try
        {
            var since = DateTime.UtcNow.Date.AddDays(-(days - 1)).ToString("yyyy-MM-dd");
            var until = DateTime.UtcNow.Date.ToString("yyyy-MM-dd");
            const string query = @"
query ($zone: string!, $since: string!, $until: string!) {
  viewer { zones(filter: {zoneTag: $zone}) {
    httpRequests1dGroups(limit: 90, filter: {date_geq: $since, date_leq: $until}, orderBy: [date_ASC]) {
      dimensions { date }
      uniq { uniques }
      sum { requests pageViews threats countryMap { clientCountryName requests } }
    }
  }}
}";
            var body = JsonSerializer.Serialize(new { query, variables = new { zone, since, until } });
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await _http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return new CloudflareResult { Configured = true, Error = $"Cloudflare API returned {(int)resp.StatusCode}. Check the API token’s permissions (Zone Analytics: Read)." };

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            if (root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0)
            {
                var msg = errs[0].TryGetProperty("message", out var m) ? m.GetString() : "unknown error";
                return new CloudflareResult { Configured = true, Error = $"Cloudflare: {msg}" };
            }

            var zones = root.GetProperty("data").GetProperty("viewer").GetProperty("zones");
            if (zones.GetArrayLength() == 0)
                return new CloudflareResult { Configured = true, Error = "No matching Cloudflare zone — check the Zone ID." };

            var stats = new CloudflareStats();
            var countries = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            foreach (var g in zones[0].GetProperty("httpRequests1dGroups").EnumerateArray())
            {
                var date = g.GetProperty("dimensions").GetProperty("date").GetString() ?? "";
                var sum = g.GetProperty("sum");
                long dayReq = sum.GetProperty("requests").GetInt64();
                long threats = sum.GetProperty("threats").GetInt64();
                long pv = sum.TryGetProperty("pageViews", out var p) ? p.GetInt64() : 0;
                long uniq = g.GetProperty("uniq").GetProperty("uniques").GetInt64();

                stats.Requests += dayReq;
                stats.Threats += threats;
                stats.PageViews += pv;
                stats.Visitors += uniq;
                stats.DayLabels.Add(DateTime.TryParse(date, out var d) ? d.ToString("dd MMM") : date);
                stats.DayRequests.Add(dayReq);
                stats.DayThreats.Add(threats);

                if (sum.TryGetProperty("countryMap", out var cm) && cm.ValueKind == JsonValueKind.Array)
                    foreach (var c in cm.EnumerateArray())
                    {
                        var name = c.GetProperty("clientCountryName").GetString() ?? "??";
                        countries[name] = countries.GetValueOrDefault(name) + c.GetProperty("requests").GetInt64();
                    }
            }

            stats.TopCountries.AddRange(countries.OrderByDescending(kv => kv.Value).Take(8)
                .Select(kv => (kv.Key, kv.Value)));

            return new CloudflareResult { Configured = true, Stats = stats };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cloudflare analytics fetch failed.");
            return new CloudflareResult { Configured = true, Error = "Couldn’t reach Cloudflare just now — try again shortly." };
        }
    }
}
