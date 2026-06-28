using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Controllers;

public class ReferralController : Controller
{
    public const string RefCookie = "sg_ref";
    private readonly IReferralService _referrals;
    public ReferralController(IReferralService referrals) => _referrals = referrals;

    /// <summary>Shareable referral link: stores the code in a cookie, then sends the visitor to the
    /// store. The referral is created when they register (see AccountController.Register).</summary>
    [AllowAnonymous]
    [HttpGet("/r/{code}")]
    public IActionResult Capture(string code)
    {
        if (!string.IsNullOrWhiteSpace(code))
        {
            Response.Cookies.Append(RefCookie, code.Trim().ToUpperInvariant(), new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddDays(30),
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Lax
            });
        }
        return RedirectToAction("Index", "Home");
    }

    /// <summary>The customer's own refer-a-friend page — code, shareable link, and stats.</summary>
    [Authorize]
    [HttpGet("/refer")]
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Refer a Friend";
        if (!await _referrals.EnabledAsync()) return NotFound();

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var stats = await _referrals.StatsForAsync(userId);
        ViewBag.Stats = stats;
        ViewBag.ShareUrl = $"{Request.Scheme}://{Request.Host}/r/{stats.Code}";
        return View();
    }
}
