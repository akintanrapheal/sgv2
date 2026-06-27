using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Controllers;

/// <summary>Public gift-card balance check. Rate-limited so the code space can't be probed.</summary>
[AllowAnonymous]
public class GiftCardsController : Controller
{
    private readonly IGiftCardService _giftCards;
    public GiftCardsController(IGiftCardService giftCards) => _giftCards = giftCards;

    [HttpGet("/gift-cards")]
    public IActionResult Index() => View();

    [HttpPost("/gift-cards")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Index(string? code)
    {
        var result = await _giftCards.CheckBalanceAsync(code);
        ViewBag.Searched = true;
        ViewBag.Code = code;
        ViewBag.Ok = result.Ok;
        ViewBag.Message = result.Message;
        ViewBag.Balance = result.Balance;
        return View();
    }
}
