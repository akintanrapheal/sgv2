using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SterlingLams.Web.Services.Marketing;

namespace SterlingLams.Web.Controllers;

/// <summary>Public one-click unsubscribe from marketing emails (token = data-protected email).</summary>
[AllowAnonymous]
public class UnsubscribeController : Controller
{
    private readonly IMarketingService _marketing;
    public UnsubscribeController(IMarketingService marketing) => _marketing = marketing;

    [HttpGet("/unsubscribe")]
    public async Task<IActionResult> Index(string? t)
    {
        var email = string.IsNullOrWhiteSpace(t) ? null : _marketing.ReadUnsubscribeToken(t);
        if (string.IsNullOrEmpty(email))
        {
            ViewBag.Ok = false;
            return View();
        }
        await _marketing.SuppressAsync(email, "Unsubscribed via email link");
        ViewBag.Ok = true;
        ViewBag.Email = email;
        return View();
    }
}
