using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SterlingLams.Web.Controllers;

/// <summary>
/// The POS selling app moved from /Till to /Pos. This keeps old bookmarks working by redirecting
/// any /Till* request to the matching /Pos* path (preserving the tail + query string).
/// </summary>
[AllowAnonymous]
public class TillRedirectController : Controller
{
    [Route("/Till")]
    [Route("/Till/{**rest}")]
    public IActionResult ToPos()
    {
        var path = Request.Path.Value ?? "/Till";
        var tail = path.Length >= 5 ? path.Substring(5) : string.Empty; // strip leading "/Till"
        return Redirect("/Pos" + tail + Request.QueryString);
    }
}
