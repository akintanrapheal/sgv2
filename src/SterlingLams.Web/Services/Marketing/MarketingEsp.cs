using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Services.Marketing;

/// <summary>Resolved marketing-send configuration. The transactional path (orders, password reset)
/// always uses SMTP; this only governs campaign blasts, where a dedicated ESP gives the throughput
/// and deliverability a shared mailbox can't. SES uses AWS keys + region; Resend/Brevo use ApiKey.</summary>
public sealed record EspConfig(string Provider, string ApiKey, string FromAddress, string FromName, string? ReplyTo, int BatchSize,
    string AwsAccessKey = "", string AwsSecretKey = "", string AwsRegion = "")
{
    /// <summary>True when an external provider is selected AND fully usable. Otherwise the sender falls
    /// back to SMTP, so a half-configured ESP never silently drops mail.</summary>
    public bool IsExternal =>
        !string.IsNullOrWhiteSpace(FromAddress) && Provider switch
        {
            "resend" or "brevo" => !string.IsNullOrWhiteSpace(ApiKey),
            "ses" => !string.IsNullOrWhiteSpace(AwsAccessKey)
                  && !string.IsNullOrWhiteSpace(AwsSecretKey)
                  && !string.IsNullOrWhiteSpace(AwsRegion),
            _ => false
        };
}

public interface IMarketingEsp
{
    Task<EspConfig> GetConfigAsync(CancellationToken ct = default);

    /// <summary>Sends one fully-rendered HTML email through the configured provider. Never throws —
    /// returns (false, reason) so the campaign sender records a per-recipient failure and moves on.</summary>
    Task<(bool ok, string? error)> SendAsync(EspConfig cfg, string toEmail, string? toName, string subject, string html, CancellationToken ct = default);

    /// <summary>Read-only key check for the Admin "Test" button (no email sent).</summary>
    Task<(bool ok, string detail)> TestKeyAsync(string provider, CancellationToken ct = default);
}

public class MarketingEsp : IMarketingEsp
{
    private readonly IHttpClientFactory _http;
    private readonly ISettingsService _settings;
    private readonly ISettingsSecretProtector _secrets;
    private readonly IConfiguration _config;
    private readonly ILogger<MarketingEsp> _log;

    public MarketingEsp(IHttpClientFactory http, ISettingsService settings, ISettingsSecretProtector secrets,
        IConfiguration config, ILogger<MarketingEsp> log)
    {
        _http = http;
        _settings = settings;
        _secrets = secrets;
        _config = config;
        _log = log;
    }

    public async Task<EspConfig> GetConfigAsync(CancellationToken ct = default)
    {
        var provider = (await _settings.GetAsync("marketing.esp.provider", "smtp")).Trim().ToLowerInvariant();
        var key = await RevealAsync("marketing.esp.api_key", "Marketing:Esp:ApiKey");

        var from = (await _settings.GetAsync("marketing.esp.from_address", "")).Trim();
        if (string.IsNullOrWhiteSpace(from)) from = (await _settings.GetAsync("email.smtp.from_address", "")).Trim();

        var fromName = (await _settings.GetAsync("marketing.esp.from_name", "")).Trim();
        if (string.IsNullOrWhiteSpace(fromName)) fromName = (await _settings.GetAsync("email.from_name", "")).Trim();
        if (string.IsNullOrWhiteSpace(fromName)) fromName = (await _settings.GetAsync("email.smtp.from_name", "Sterlin Glams")).Trim();
        if (string.IsNullOrWhiteSpace(fromName)) fromName = "Sterlin Glams";

        var replyTo = (await _settings.GetAsync("email.reply_to", "")).Trim();
        var batch = await _settings.GetIntAsync("marketing.esp.batch_size", 100);
        batch = Math.Clamp(batch, 1, 500);

        var awsAccess = await RevealAsync("marketing.esp.aws_access_key", "Marketing:Esp:AwsAccessKey");
        var awsSecret = await RevealAsync("marketing.esp.aws_secret_key", "Marketing:Esp:AwsSecretKey");
        var awsRegion = (await _settings.GetAsync("marketing.esp.aws_region", "us-east-1")).Trim();

        return new EspConfig(provider, key, from, fromName, string.IsNullOrWhiteSpace(replyTo) ? null : replyTo, batch,
            awsAccess, awsSecret, awsRegion);
    }

    private async Task<string> RevealAsync(string key, string? configKey)
    {
        var raw = await _settings.GetAsync(key, "");
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try { return _secrets.Reveal(raw); } catch { return ""; }
        }
        return configKey is null ? "" : (_config[configKey] ?? "");
    }

    public async Task<(bool ok, string? error)> SendAsync(EspConfig cfg, string toEmail, string? toName, string subject, string html, CancellationToken ct = default)
    {
        try
        {
            var http = _http.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(30);

            if (cfg.Provider == "resend")
            {
                var payload = new Dictionary<string, object?>
                {
                    ["from"] = string.IsNullOrWhiteSpace(cfg.FromName) ? cfg.FromAddress : $"{cfg.FromName} <{cfg.FromAddress}>",
                    ["to"] = new[] { toEmail },
                    ["subject"] = subject,
                    ["html"] = html,
                };
                if (cfg.ReplyTo != null) payload["reply_to"] = cfg.ReplyTo;

                using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails") { Content = Json(payload) };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
                using var resp = await http.SendAsync(req, ct);
                return resp.IsSuccessStatusCode ? (true, null) : (false, await ErrAsync(resp));
            }

            if (cfg.Provider == "brevo")
            {
                var payload = new Dictionary<string, object?>
                {
                    ["sender"] = new { name = cfg.FromName, email = cfg.FromAddress },
                    ["to"] = new[] { new { email = toEmail, name = string.IsNullOrWhiteSpace(toName) ? toEmail : toName } },
                    ["subject"] = subject,
                    ["htmlContent"] = html,
                };
                if (cfg.ReplyTo != null) payload["replyTo"] = new { email = cfg.ReplyTo };

                using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.brevo.com/v3/smtp/email") { Content = Json(payload) };
                req.Headers.Add("api-key", cfg.ApiKey);
                using var resp = await http.SendAsync(req, ct);
                return resp.IsSuccessStatusCode ? (true, null) : (false, await ErrAsync(resp));
            }

            if (cfg.Provider == "ses")
            {
                using var client = SesClient(cfg);
                var req = new SendEmailRequest
                {
                    FromEmailAddress = string.IsNullOrWhiteSpace(cfg.FromName) ? cfg.FromAddress : $"{cfg.FromName} <{cfg.FromAddress}>",
                    Destination = new Destination { ToAddresses = new List<string> { toEmail } },
                    Content = new EmailContent
                    {
                        Simple = new Message
                        {
                            Subject = new Content { Data = subject, Charset = "UTF-8" },
                            Body = new Body { Html = new Content { Data = html, Charset = "UTF-8" } }
                        }
                    }
                };
                if (cfg.ReplyTo != null) req.ReplyToAddresses = new List<string> { cfg.ReplyTo };
                var resp = await client.SendEmailAsync(req, ct);
                return string.IsNullOrEmpty(resp.MessageId) ? (false, "SES returned no message id") : (true, null);
            }

            return (false, "No external ESP selected.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ESP send failed via {Provider}", cfg.Provider);
            return (false, ex.Message.Length > 280 ? ex.Message[..280] : ex.Message);
        }
    }

    public async Task<(bool ok, string detail)> TestKeyAsync(string provider, CancellationToken ct = default)
    {
        provider = (provider ?? "").Trim().ToLowerInvariant();

        if (provider == "ses")
        {
            var cfg = await GetConfigAsync(ct);
            if (string.IsNullOrWhiteSpace(cfg.AwsAccessKey) || string.IsNullOrWhiteSpace(cfg.AwsSecretKey))
                return (false, "No AWS access key / secret saved yet.");
            try
            {
                using var client = SesClient(cfg);
                var acct = await client.GetAccountAsync(new GetAccountRequest(), ct);
                if (acct.SendingEnabled == false) return (false, "SES reachable, but sending is paused on this account.");
                return acct.ProductionAccessEnabled == true
                    ? (true, "Connected — SES ready (production access).")
                    : (true, "Connected — SES in SANDBOX: you can only send to verified addresses until you request production access.");
            }
            catch (Exception ex) { return (false, ex.Message.Length > 200 ? ex.Message[..200] : ex.Message); }
        }

        var key = await RevealAsync("marketing.esp.api_key", "Marketing:Esp:ApiKey");
        if (provider is not ("resend" or "brevo")) return (false, "SMTP selected — no ESP key to test.");
        if (string.IsNullOrWhiteSpace(key)) return (false, $"No {Title(provider)} API key saved yet.");

        try
        {
            var http = _http.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(12);
            if (provider == "resend")
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.resend.com/domains");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var resp = await http.SendAsync(req, ct);
                return resp.IsSuccessStatusCode
                    ? (true, "Connected — Resend API key valid.")
                    : (false, (int)resp.StatusCode == 401 ? "Resend key rejected (401)." : $"Resend returned {(int)resp.StatusCode}.");
            }
            using var breq = new HttpRequestMessage(HttpMethod.Get, "https://api.brevo.com/v3/account");
            breq.Headers.Add("api-key", key);
            using var bresp = await http.SendAsync(breq, ct);
            return bresp.IsSuccessStatusCode
                ? (true, "Connected — Brevo API key valid.")
                : (false, (int)bresp.StatusCode == 401 ? "Brevo key rejected (401)." : $"Brevo returned {(int)bresp.StatusCode}.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static string Title(string p) => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..];

    /// <summary>A short-lived SESv2 client for the configured AWS keys + region. Unknown region strings
    /// resolve to a usable endpoint; the send/test call surfaces any credential/region error.</summary>
    private static AmazonSimpleEmailServiceV2Client SesClient(EspConfig cfg)
    {
        var region = RegionEndpoint.GetBySystemName(string.IsNullOrWhiteSpace(cfg.AwsRegion) ? "us-east-1" : cfg.AwsRegion);
        return new AmazonSimpleEmailServiceV2Client(new BasicAWSCredentials(cfg.AwsAccessKey, cfg.AwsSecretKey), region);
    }

    private static StringContent Json(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static async Task<string> ErrAsync(HttpResponseMessage resp)
    {
        string body = "";
        try { body = await resp.Content.ReadAsStringAsync(); } catch { }
        if (body.Length > 240) body = body[..240];
        return $"HTTP {(int)resp.StatusCode}{(string.IsNullOrWhiteSpace(body) ? "" : ": " + body)}";
    }
}
