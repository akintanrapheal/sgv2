using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace SterlingLams.Web.Services;

/// <summary>Reports pulled from the Google Analytics Data API (GA4) for the Admin → Google Analytics
/// page: users, sessions, page views, bounce rate, average session duration, a daily series and the top
/// pages. Authenticates with a service-account key (stored encrypted) via a signed JWT → access token —
/// no external Google SDK. Everything degrades gracefully: not configured → Configured false; any
/// API/parse error → a friendly Error, never a throw into the request.</summary>
public interface IGoogleAnalytics
{
    Task<bool> IsConfiguredAsync();
    Task<GaResult> GetAsync(int days);
}

public class GaStats
{
    public long Users { get; set; }
    public long Sessions { get; set; }
    public long PageViews { get; set; }
    public double BounceRate { get; set; }          // 0..1
    public double AvgSessionDuration { get; set; }   // seconds
    public List<string> DayLabels { get; } = new();
    public List<long> DayUsers { get; } = new();
    public List<long> DayViews { get; } = new();
    public List<(string Path, long Views)> TopPages { get; } = new();
}

public class GaResult
{
    public bool Configured { get; set; }
    public string? Error { get; set; }
    public GaStats? Stats { get; set; }
}

public class GoogleAnalyticsService : IGoogleAnalytics
{
    private const string TokenUri = "https://oauth2.googleapis.com/token";
    private const string Scope = "https://www.googleapis.com/auth/analytics.readonly";

    private readonly HttpClient _http;
    private readonly ISettingsService _settings;
    private readonly ISettingsSecretProtector _secrets;
    private readonly IMemoryCache _cache;
    private readonly ILogger<GoogleAnalyticsService> _log;

    public GoogleAnalyticsService(HttpClient http, ISettingsService settings,
        ISettingsSecretProtector secrets, IMemoryCache cache, ILogger<GoogleAnalyticsService> log)
    {
        _http = http;
        _settings = settings;
        _secrets = secrets;
        _cache = cache;
        _log = log;
    }

    public async Task<bool> IsConfiguredAsync()
    {
        if (!await _settings.GetBoolAsync("ga.enabled", false)) return false;
        var prop = (await _settings.GetAsync("ga.property_id", "")).Trim();
        var key = _secrets.Reveal(await _settings.GetAsync("ga.service_account_json", ""));
        return prop.Length > 0 && key.Contains("private_key");
    }

    public async Task<GaResult> GetAsync(int days)
    {
        if (days is < 1 or > 365) days = 28;
        var propertyId = new string((await _settings.GetAsync("ga.property_id", "")).Where(char.IsDigit).ToArray());
        var saJson = _secrets.Reveal(await _settings.GetAsync("ga.service_account_json", ""));
        if (!await _settings.GetBoolAsync("ga.enabled", false))
            return new GaResult { Configured = false };
        if (propertyId.Length == 0 || !saJson.Contains("private_key"))
            return new GaResult { Configured = false, Error = "Add the GA4 Property ID and a service-account key in Admin → Integrations." };

        try
        {
            var token = await GetAccessTokenAsync(saJson);
            if (token == null) return new GaResult { Configured = true, Error = "Could not authenticate with the service-account key. Check the JSON and that the Analytics Data API is enabled." };

            // One round-trip: totals, a daily series, and the top pages.
            var body = new
            {
                requests = new object[]
                {
                    new { metrics = new[] {
                            new{name="totalUsers"}, new{name="sessions"}, new{name="screenPageViews"},
                            new{name="bounceRate"}, new{name="averageSessionDuration"} },
                          dateRanges = new[] { new { startDate = $"{days - 1}daysAgo", endDate = "today" } } },
                    new { dimensions = new[] { new{name="date"} },
                          metrics = new[] { new{name="totalUsers"}, new{name="screenPageViews"} },
                          dateRanges = new[] { new { startDate = $"{days - 1}daysAgo", endDate = "today" } },
                          orderBys = new[] { new { dimension = new { dimensionName = "date" } } } },
                    new { dimensions = new[] { new{name="pagePath"} },
                          metrics = new[] { new{name="screenPageViews"} },
                          dateRanges = new[] { new { startDate = $"{days - 1}daysAgo", endDate = "today" } },
                          orderBys = new[] { new { metric = new { metricName = "screenPageViews" }, desc = true } },
                          limit = 10 },
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"https://analyticsdata.googleapis.com/v1beta/properties/{propertyId}:batchRunReports");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req);
            var payload = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("GA Data API {Status}: {Body}", (int)resp.StatusCode, Truncate(payload));
                var msg = resp.StatusCode == System.Net.HttpStatusCode.Forbidden
                    ? "Access denied — add the service-account email as a Viewer on the GA4 property."
                    : $"Google Analytics returned {(int)resp.StatusCode}.";
                return new GaResult { Configured = true, Error = msg };
            }

            return new GaResult { Configured = true, Stats = Parse(payload) };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "GA Data API call failed.");
            return new GaResult { Configured = true, Error = "Couldn't reach Google Analytics. Please try again." };
        }
    }

    private static GaStats Parse(string payload)
    {
        var st = new GaStats();
        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("reports", out var reports)) return st;
        var arr = reports.EnumerateArray().ToList();

        // Report 0 — totals (single row).
        if (arr.Count > 0 && arr[0].TryGetProperty("rows", out var totRows) && totRows.GetArrayLength() > 0)
        {
            var m = totRows[0].GetProperty("metricValues");
            st.Users = L(m, 0); st.Sessions = L(m, 1); st.PageViews = L(m, 2);
            st.BounceRate = D(m, 3); st.AvgSessionDuration = D(m, 4);
        }
        // Report 1 — daily series.
        if (arr.Count > 1 && arr[1].TryGetProperty("rows", out var dayRows))
            foreach (var r in dayRows.EnumerateArray())
            {
                var d = r.GetProperty("dimensionValues")[0].GetString() ?? "";   // yyyyMMdd
                st.DayLabels.Add(d.Length == 8 ? $"{d[6..8]}/{d[4..6]}" : d);
                var m = r.GetProperty("metricValues");
                st.DayUsers.Add(L(m, 0)); st.DayViews.Add(L(m, 1));
            }
        // Report 2 — top pages.
        if (arr.Count > 2 && arr[2].TryGetProperty("rows", out var pageRows))
            foreach (var r in pageRows.EnumerateArray())
            {
                var path = r.GetProperty("dimensionValues")[0].GetString() ?? "";
                st.TopPages.Add((path, L(r.GetProperty("metricValues"), 0)));
            }
        return st;
    }

    private static long L(JsonElement metrics, int i)
        => i < metrics.GetArrayLength() && long.TryParse(metrics[i].GetProperty("value").GetString(), out var v) ? v : 0;
    private static double D(JsonElement metrics, int i)
        => i < metrics.GetArrayLength() && double.TryParse(metrics[i].GetProperty("value").GetString(),
               System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static string Truncate(string s) => s.Length > 400 ? s[..400] : s;

    // ── Service-account auth: signed JWT → cached access token ──────────────────────────────────
    private async Task<string?> GetAccessTokenAsync(string saJson)
    {
        using var doc = JsonDocument.Parse(saJson);
        var root = doc.RootElement;
        var clientEmail = root.GetProperty("client_email").GetString() ?? "";
        var privateKey = root.GetProperty("private_key").GetString() ?? "";
        var tokenUri = root.TryGetProperty("token_uri", out var t) ? (t.GetString() ?? TokenUri) : TokenUri;
        if (clientEmail.Length == 0 || privateKey.Length == 0) return null;

        var cacheKey = "ga.token." + clientEmail;
        if (_cache.TryGetValue(cacheKey, out string? cached) && cached != null) return cached;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = B64Url("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"u8.ToArray());
        var claims = B64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["iss"] = clientEmail, ["scope"] = Scope, ["aud"] = tokenUri, ["iat"] = now, ["exp"] = now + 3600
        })));
        var signingInput = header + "." + claims;

        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKey);
        var sig = B64Url(rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var jwt = signingInput + "." + sig;

        using var req = new HttpRequestMessage(HttpMethod.Post, tokenUri)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = jwt
            })
        };
        using var resp = await _http.SendAsync(req);
        var payload = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("GA token exchange {Status}: {Body}", (int)resp.StatusCode, Truncate(payload));
            return null;
        }
        using var td = JsonDocument.Parse(payload);
        var token = td.RootElement.GetProperty("access_token").GetString();
        var expires = td.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        if (!string.IsNullOrEmpty(token))
            _cache.Set(cacheKey, token, TimeSpan.FromSeconds(Math.Max(60, expires - 120)));
        return token;
    }

    private static string B64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
