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
}

public class SmtpEmailService : IEmailService
{
    private readonly EmailOptions _opt;
    private readonly ILogger<SmtpEmailService> _log;
    private readonly IWebHostEnvironment _env;

    public SmtpEmailService(IOptions<EmailOptions> opt, ILogger<SmtpEmailService> log, IWebHostEnvironment env)
    {
        _opt = opt.Value;
        _log = log;
        _env = env;
    }

    public async Task<bool> SendAsync(string toEmail, string subject, string innerHtml, string? toName = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(toEmail))
            return false;

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

            return await WriteToPickupAsync(pickupDir, toEmail, toName, subject, innerHtml);
        }

        try
        {
            using var msg = new MailMessage
            {
                From = new MailAddress(_opt.FromAddress, _opt.FromName),
                Subject = subject,
                Body = Wrap(subject, innerHtml),
                IsBodyHtml = true,
            };
            msg.To.Add(new MailAddress(toEmail, toName ?? toEmail));

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

    /// <summary>Dev/preview fallback: write the email to disk instead of sending it. Returns true so
    /// callers behave as if delivered (e.g. mark notified). Never throws.</summary>
    private async Task<bool> WriteToPickupAsync(string pickupDir, string toEmail, string? toName, string subject, string innerHtml)
    {
        try
        {
            var dir = Path.IsPathRooted(pickupDir) ? pickupDir : Path.Combine(_env.ContentRootPath, pickupDir);
            Directory.CreateDirectory(dir);
            var safeTo = string.Concat(toEmail.Split(Path.GetInvalidFileNameChars()));
            var file = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}_{safeTo}.html");
            var header = $"<!-- To: {System.Net.WebUtility.HtmlEncode(toName ?? toEmail)} <{System.Net.WebUtility.HtmlEncode(toEmail)}> | Subject: {System.Net.WebUtility.HtmlEncode(subject)} -->\n";
            await File.WriteAllTextAsync(file, header + Wrap(subject, innerHtml));
            _log.LogInformation("Email written to pickup folder (no SMTP): {File} — to={To} subject=\"{Subject}\"", file, toEmail, subject);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to write email to pickup folder. to={To} subject=\"{Subject}\"", toEmail, subject);
            return false;
        }
    }

    /// <summary>Wraps content in a minimal branded HTML shell (inline styles for email clients).</summary>
    private string Wrap(string title, string content) => $@"<!DOCTYPE html>
<html><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1""></head>
<body style=""margin:0;padding:0;background:#f5f5f4;font-family:Helvetica,Arial,sans-serif;color:#1c1917;"">
  <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:#f5f5f4;padding:32px 0;"">
    <tr><td align=""center"">
      <table role=""presentation"" width=""560"" cellpadding=""0"" cellspacing=""0"" style=""width:560px;max-width:92%;background:#ffffff;border:1px solid #e7e5e4;"">
        <tr><td style=""background:#0a0a0a;padding:24px 32px;text-align:center;"">
          <span style=""color:#ffffff;font-size:20px;letter-spacing:3px;text-transform:uppercase;"">{System.Net.WebUtility.HtmlEncode(_opt.FromName)}</span>
        </td></tr>
        <tr><td style=""padding:32px;font-size:15px;line-height:1.6;color:#292524;"">
          {content}
        </td></tr>
        <tr><td style=""padding:20px 32px;border-top:1px solid #e7e5e4;text-align:center;font-size:11px;color:#a8a29e;"">
          &copy; {DateTime.UtcNow:yyyy} {System.Net.WebUtility.HtmlEncode(_opt.FromName)}. This is an automated message — please don't reply.
        </td></tr>
      </table>
    </td></tr>
  </table>
</body></html>";
}
