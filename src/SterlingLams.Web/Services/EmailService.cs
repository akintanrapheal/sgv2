using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace SterlingLams.Web.Services;

/// <summary>SMTP configuration, bound from the "Email" config section.</summary>
public class EmailOptions
{
    /// <summary>Master switch. When false (or Host blank), sends are skipped and logged.</summary>
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "Sterlin Glams";
    public bool EnableSsl { get; set; } = true;

    /// <summary>When SMTP isn't configured, write emails here as .html files instead of sending
    /// (a "pickup folder" for dev/preview). Relative paths resolve under the content root. If blank,
    /// this defaults to App_Data/sent-emails in Development only; in Production sends are skipped.</summary>
    public string PickupDirectory { get; set; } = "";
}

public interface IEmailService
{
    /// <summary>
    /// Sends a branded HTML email. The body is wrapped in the site shell automatically.
    /// Never throws — returns false on failure (or when SMTP isn't configured) so callers
    /// (checkout, password reset) are never broken by mail problems.
    /// </summary>
    Task<bool> SendAsync(string toEmail, string subject, string innerHtml, string? toName = null, CancellationToken ct = default);

    /// <summary>Renders the branded email shell around the given inner HTML — for the admin preview.</summary>
    Task<string> RenderAsync(string subject, string innerHtml);
}

public class SmtpEmailService : IEmailService
{
    private readonly EmailOptions _opt;
    private readonly ILogger<SmtpEmailService> _log;
    private readonly IWebHostEnvironment _env;
    private readonly ISettingsService _settings;
    private readonly IConfiguration _config;

    public SmtpEmailService(IOptions<EmailOptions> opt, ILogger<SmtpEmailService> log, IWebHostEnvironment env,
        ISettingsService settings, IConfiguration config)
    {
        _opt = opt.Value;
        _log = log;
        _env = env;
        _settings = settings;
        _config = config;
    }

    /// <summary>Admin-customizable email branding (Settings → Emails), resolved per send.</summary>
    private sealed record Branding(string FromName, string ReplyTo, string HeaderColor, string FooterText, string? LogoUrl);

    private async Task<Branding> GetBrandingAsync()
    {
        var fromName = await _settings.GetAsync("email.from_name", _opt.FromName);
        if (string.IsNullOrWhiteSpace(fromName)) fromName = _opt.FromName;
        var replyTo = await _settings.GetAsync("email.reply_to", "");
        var headerColor = await _settings.GetAsync("email.header_color", "#0a0a0a");
        var footerText = await _settings.GetAsync("email.footer_text", "This is an automated message — please don't reply.");

        // Logo only renders in email if we can build an absolute URL (clients won't load relative paths).
        var logo = await _settings.GetAsync("general.logo_url", "");
        string? logoUrl = null;
        if (!string.IsNullOrWhiteSpace(logo))
        {
            if (logo.StartsWith("http", StringComparison.OrdinalIgnoreCase)) logoUrl = logo;
            else
            {
                var baseUrl = (_config["App:BaseUrl"] ?? "").TrimEnd('/');
                if (!string.IsNullOrEmpty(baseUrl)) logoUrl = baseUrl + "/" + logo.TrimStart('/');
            }
        }
        return new Branding(fromName, replyTo, headerColor, footerText, logoUrl);
    }

    public async Task<bool> SendAsync(string toEmail, string subject, string innerHtml, string? toName = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(toEmail))
            return false;

        var brand = await GetBrandingAsync();

        var smtpConfigured = _opt.Enabled && !string.IsNullOrWhiteSpace(_opt.Host) && !string.IsNullOrWhiteSpace(_opt.FromAddress);
        if (!smtpConfigured)
        {
            // No SMTP: write to a pickup folder if one's set or we're in Development; otherwise skip.
            var pickupDir = _opt.PickupDirectory;
            if (string.IsNullOrWhiteSpace(pickupDir) && _env.IsDevelopment())
                pickupDir = "App_Data/sent-emails";

            if (string.IsNullOrWhiteSpace(pickupDir))
            {
                _log.LogWarning("Email NOT sent (SMTP not configured). to={To} subject=\"{Subject}\"", toEmail, subject);
                return false;
            }

            return await WriteToPickupAsync(pickupDir, toEmail, toName, subject, innerHtml, brand);
        }

        try
        {
            using var msg = new MailMessage
            {
                From = new MailAddress(_opt.FromAddress, brand.FromName),
                Subject = subject,
                Body = Wrap(subject, innerHtml, brand),
                IsBodyHtml = true,
            };
            msg.To.Add(new MailAddress(toEmail, toName ?? toEmail));
            if (!string.IsNullOrWhiteSpace(brand.ReplyTo))
                msg.ReplyToList.Add(new MailAddress(brand.ReplyTo));

            using var client = new SmtpClient(_opt.Host, _opt.Port)
            {
                EnableSsl = _opt.EnableSsl,
                Credentials = new NetworkCredential(_opt.Username, _opt.Password),
            };
            await client.SendMailAsync(msg, ct);
            _log.LogInformation("Email sent to {To}: \"{Subject}\"", toEmail, subject);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to send email to {To}: \"{Subject}\"", toEmail, subject);
            return false;
        }
    }

    public async Task<string> RenderAsync(string subject, string innerHtml)
        => Wrap(subject, innerHtml, await GetBrandingAsync());

    /// <summary>Dev/preview fallback: write the email to disk instead of sending it. Returns true so
    /// callers behave as if delivered (e.g. mark notified). Never throws.</summary>
    private async Task<bool> WriteToPickupAsync(string pickupDir, string toEmail, string? toName, string subject, string innerHtml, Branding brand)
    {
        try
        {
            var dir = Path.IsPathRooted(pickupDir) ? pickupDir : Path.Combine(_env.ContentRootPath, pickupDir);
            Directory.CreateDirectory(dir);
            var safeTo = string.Concat(toEmail.Split(Path.GetInvalidFileNameChars()));
            var file = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}_{safeTo}.html");
            var header = $"<!-- To: {System.Net.WebUtility.HtmlEncode(toName ?? toEmail)} <{System.Net.WebUtility.HtmlEncode(toEmail)}> | From: {System.Net.WebUtility.HtmlEncode(brand.FromName)} | Subject: {System.Net.WebUtility.HtmlEncode(subject)} -->\n";
            await File.WriteAllTextAsync(file, header + Wrap(subject, innerHtml, brand));
            _log.LogInformation("Email written to pickup folder (no SMTP): {File} — to={To} subject=\"{Subject}\"", file, toEmail, subject);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to write email to pickup folder. to={To} subject=\"{Subject}\"", toEmail, subject);
            return false;
        }
    }

    /// <summary>Wraps content in a minimal branded HTML shell (inline styles for email clients),
    /// using the admin-customizable branding (header colour, logo, sender name, footer).</summary>
    private string Wrap(string title, string content, Branding brand)
    {
        var headerInner = string.IsNullOrEmpty(brand.LogoUrl)
            ? $@"<span style=""color:#ffffff;font-size:20px;letter-spacing:3px;text-transform:uppercase;"">{System.Net.WebUtility.HtmlEncode(brand.FromName)}</span>"
            : $@"<img src=""{brand.LogoUrl}"" alt=""{System.Net.WebUtility.HtmlEncode(brand.FromName)}"" style=""max-height:40px;""/>";
        return $@"<!DOCTYPE html>
<html><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1""></head>
<body style=""margin:0;padding:0;background:#f5f5f4;font-family:Helvetica,Arial,sans-serif;color:#1c1917;"">
  <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:#f5f5f4;padding:32px 0;"">
    <tr><td align=""center"">
      <table role=""presentation"" width=""560"" cellpadding=""0"" cellspacing=""0"" style=""width:560px;max-width:92%;background:#ffffff;border:1px solid #e7e5e4;"">
        <tr><td style=""background:{System.Net.WebUtility.HtmlEncode(brand.HeaderColor)};padding:24px 32px;text-align:center;"">
          {headerInner}
        </td></tr>
        <tr><td style=""padding:32px;font-size:15px;line-height:1.6;color:#292524;"">
          {content}
        </td></tr>
        <tr><td style=""padding:20px 32px;border-top:1px solid #e7e5e4;text-align:center;font-size:11px;color:#a8a29e;"">
          &copy; {DateTime.UtcNow:yyyy} {System.Net.WebUtility.HtmlEncode(brand.FromName)}. {System.Net.WebUtility.HtmlEncode(brand.FooterText)}
        </td></tr>
      </table>
    </td></tr>
  </table>
</body></html>";
    }
}
