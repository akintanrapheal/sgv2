using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SterlingLams.Web.Infrastructure;

namespace SterlingLams.Web.Controllers;

/// <summary>
/// Keeps old POS URLs working after the Stage 1/2 refactor:
///  • the selling app moved /Till → /Pos
///  • POS management moved out of the website Admin into the Inventory System
/// Any legacy /Till*, /Admin/Pos* or /Admin/Registers* request is redirected to its new home.
/// </summary>
[AllowAnonymous]
public class TillRedirectController : Controller
{
    // Selling app: /Till* → /Pos* (preserve tail + query)
    [Route("/Till")]
    [Route("/Till/{**rest}")]
    public IActionResult ToPos()
    {
        // Once POS sits behind a secret prefix, /Pos itself 404s — so this redirect would only point
        // at a dead path while confirming a till exists. Behave exactly like /Pos does: 404.
        if (StaffPaths.PosIsSecret) return NotFound();
        var path = Request.Path.Value ?? "/Till";
        var tail = path.Length >= 5 ? path.Substring(5) : string.Empty; // strip "/Till"
        return Redirect("/Pos" + tail + Request.QueryString);
    }

    // POS management moved to the Inventory System.
    [Route("/Admin/Pos")]
    [Route("/Admin/Pos/{**rest}")]
    public IActionResult AdminPosToInventory(string? rest)
    {
        // These are literal /Admin/* routes, so they resolve even when the Admin area itself is
        // secret-prefixed (and /Admin 404s). Redirecting would hand an anonymous caller a Location
        // header containing the secret Inventory/POS path — leaking exactly what the prefix hides.
        if (StaffPaths.AdminIsSecret || StaffPaths.InventoryIsSecret || StaffPaths.PosIsSecret)
            return NotFound();

        rest = (rest ?? string.Empty).Trim('/');
        // Receipt printing now lives in the POS app.
        if (rest.StartsWith("Receipt/", System.StringComparison.OrdinalIgnoreCase))
            return Redirect("/Pos/" + rest);
        if (rest.StartsWith("DiscountReasons", System.StringComparison.OrdinalIgnoreCase))
            return Redirect($"/{StaffPaths.Inventory}/Till/DiscountReasons");
        if (rest.StartsWith("Sales", System.StringComparison.OrdinalIgnoreCase))
            return Redirect($"/{StaffPaths.Inventory}/Sales/Completed");
        // Sessions / Index / anything else → POS oversight
        return Redirect($"/{StaffPaths.Inventory}/Till");
    }

    // Register management moved to the Inventory System.
    [Route("/Admin/Registers")]
    [Route("/Admin/Registers/{**rest}")]
    public IActionResult AdminRegistersToInventory() =>
        StaffPaths.AdminIsSecret || StaffPaths.InventoryIsSecret
            ? NotFound()
            : Redirect($"/{StaffPaths.Inventory}/Org/Registers");
}
