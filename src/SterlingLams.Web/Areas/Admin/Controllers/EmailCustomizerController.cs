using Microsoft.AspNetCore.Mvc;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers;

public class EmailCustomizerController : AdminBaseController
{
    protected override string Section => "Emails";

    private readonly ISettingsService _settings;
    private readonly IEmailService _email;

    public EmailCustomizerController(ISettingsService settings, IEmailService email)
    {
        _settings = settings;
        _email = email;
    }

    // The customer-facing emails whose subject + intro are editable.
    public static readonly (string Key, string Label, string DefaultSubject, string DefaultIntro)[] Types = new[]
    {
        ("order_confirmed", "Order confirmation", "Your order is confirmed", "Thank you for your order — here's your summary. We'll email you when it's on the way."),
        ("back_in_stock",   "Back in stock",      "Good news — it's back in stock", "An item you wanted is available again. These pieces sell quickly, so don't wait."),
        ("abandoned_cart",  "Abandoned cart",     "You left something in your bag", "You have items waiting in your bag — we've saved them for you."),
        ("password_reset",  "Password reset",     "Reset your password", "We received a request to reset your password. Click below to choose a new one. This link expires shortly."),
        ("email_confirm",   "Email confirmation", "Confirm your email", "Thanks for creating an account with us. Please confirm this is your email address by clicking below."),
    };

    public async Task<IActionResult> Index(string type = "order_confirmed")
    {
        if (!Types.Any(t => t.Key == type)) type = "order_confirmed";
        ViewData["Title"] = "Email Customizer";

        var def = Types.First(t => t.Key == type);
        var vm = new EmailCustomizerViewModel
        {
            Type = type,
            Types = Types.Select(t => (t.Key, t.Label)).ToList(),
            Subject = await _settings.GetAsync($"email.{type}.subject", def.DefaultSubject),
            Intro = await _settings.GetAsync($"email.{type}.intro", def.DefaultIntro),
            FromName = await _settings.GetAsync("email.from_name", "Sterlin Glams"),
            ReplyTo = await _settings.GetAsync("email.reply_to", ""),
            HeaderColor = await _settings.GetAsync("email.header_color", "#0a0a0a"),
            FooterText = await _settings.GetAsync("email.footer_text", "This is an automated message — please don't reply."),
        };
        return View(vm);
    }

    // Full rendered email HTML for the live-preview iframe (loaded via srcdoc, not framed).
    // Optional subject/intro overrides let the preview reflect unsaved edits as the admin types.
    public async Task<IActionResult> Preview(string type, string? subject = null, string? intro = null)
    {
        var (s, body) = await BuildSampleAsync(type, subject, intro);
        var html = await _email.RenderAsync(s, body);
        return Content(html, "text/html");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(string type, string? subject, string? intro,
        string? fromName, string? replyTo, string? headerColor, string? footerText)
    {
        if (!Types.Any(t => t.Key == type))
        {
            TempData["Error"] = "Unknown email type.";
            return RedirectToAction(nameof(Index));
        }
        await _settings.SaveManyAsync(new Dictionary<string, string>
        {
            [$"email.{type}.subject"] = subject?.Trim() ?? "",
            [$"email.{type}.intro"]   = intro?.Trim() ?? "",
            ["email.from_name"]       = string.IsNullOrWhiteSpace(fromName) ? "Sterlin Glams" : fromName.Trim(),
            ["email.reply_to"]        = replyTo?.Trim() ?? "",
            ["email.header_color"]    = string.IsNullOrWhiteSpace(headerColor) ? "#0a0a0a" : headerColor.Trim(),
            ["email.footer_text"]     = footerText?.Trim() ?? "",
        });
        await LogAsync("Update", "Setting", null, $"Updated email template '{type}' + branding");
        TempData["Success"] = "Email saved.";
        return RedirectToAction(nameof(Index), new { type });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SendTest(string type, string email)
    {
        email = (email ?? "").Trim();
        if (string.IsNullOrWhiteSpace(email) || !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email))
            return Json(new { success = false, message = "Enter a valid email address." });

        var (subject, body) = await BuildSampleAsync(type);
        var ok = await _email.SendAsync(email, "[TEST] " + subject, body);
        await LogAsync("Update", "Setting", null, $"Sent test '{type}' email to {email}");
        return Json(new { success = ok, message = ok ? $"Test sent to {email}." : "Could not send — check SMTP settings." });
    }

    // ── Sample bodies (mirror the real emails, with placeholder data) ─────────
    private async Task<(string Subject, string Body)> BuildSampleAsync(string type, string? subjectOverride = null, string? introOverride = null)
    {
        var def = Types.FirstOrDefault(t => t.Key == type);
        if (def.Key == null) { def = Types[0]; type = def.Key; }
        var subject = !string.IsNullOrWhiteSpace(subjectOverride) ? subjectOverride
            : await _settings.GetAsync($"email.{type}.subject", def.DefaultSubject);
        var intro = introOverride != null ? introOverride
            : await _settings.GetAsync($"email.{type}.intro", def.DefaultIntro);
        string E(string s) => System.Net.WebUtility.HtmlEncode(s);

        string Button(string label, string href) => $@"
            <table role=""presentation"" cellpadding=""0"" cellspacing=""0"" style=""margin:24px 0;""><tr>
                <td style=""background:#ec1c8e;border-radius:2px;""><a href=""{href}"" style=""display:inline-block;padding:12px 28px;color:#fff;font-size:13px;letter-spacing:1px;text-transform:uppercase;text-decoration:none;"">{label}</a></td>
            </tr></table>";

        string body = type switch
        {
            "order_confirmed" => $@"
                <h2 style=""font-size:18px;margin:0 0 16px;"">Thank you for your order</h2>
                <p>{E(intro)}</p>
                <p>Order <strong>SL-20260101-1234</strong>:</p>
                <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""margin:20px 0;font-size:14px;"">
                    <tr><td style=""padding:8px 0;border-bottom:1px solid #f0efed;"">Pearl Dangle Loop Earrings — Silver &times; 2</td><td align=""right"" style=""padding:8px 0;border-bottom:1px solid #f0efed;"">&#8358;30,000</td></tr>
                    <tr><td style=""padding:8px 0;border-bottom:1px solid #f0efed;"">2-Tone Band Ring &times; 1</td><td align=""right"" style=""padding:8px 0;border-bottom:1px solid #f0efed;"">&#8358;6,000</td></tr>
                    <tr><td style=""padding:12px 0 0;font-weight:bold;"">Total</td><td align=""right"" style=""padding:12px 0 0;font-weight:bold;"">&#8358;36,000</td></tr>
                </table>",
            "back_in_stock" => $@"
                <h2 style=""font-size:18px;margin:0 0 16px;"">It's back in stock</h2>
                <p>{E(intro)}</p>
                <p style=""font-size:16px;""><strong>Pearl Dangle Loop Earrings — Silver</strong></p>
                {Button("Shop now", "#")}",
            "abandoned_cart" => $@"
                <h2 style=""font-size:18px;margin:0 0 16px;"">Your bag is waiting</h2>
                <p>{E(intro)}</p>
                <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""margin:20px 0;font-size:14px;"">
                    <tr><td style=""padding:8px 0;border-bottom:1px solid #f0efed;"">2-Tone Band Ring &times; 1</td><td align=""right"" style=""padding:8px 0;border-bottom:1px solid #f0efed;"">&#8358;6,000</td></tr>
                </table>
                {Button("Return to your bag", "#")}",
            "password_reset" => $@"
                <h2 style=""font-size:18px;margin:0 0 16px;"">Reset your password</h2>
                <p>{E(intro)}</p>
                {Button("Reset password", "#")}",
            "email_confirm" => $@"
                <h2 style=""font-size:18px;margin:0 0 16px;"">Confirm your email</h2>
                <p>{E(intro)}</p>
                {Button("Confirm email", "#")}",
            _ => $"<p>{E(intro)}</p>"
        };
        return (subject, body);
    }
}
