using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.Controllers;

/// <summary>
/// Email open-tracking pixel. Each sent email embeds a 1×1 image at <c>/e/o/{trackId}.png</c>; when the
/// recipient's mail client loads it, we stamp the matching <see cref="Models.Domain.EmailLog"/> row as
/// opened. Public + anonymous (the request comes from the mail client, not a signed-in session) and
/// always returns the pixel, even if the token is unknown, so it never shows a broken image.
/// </summary>
[AllowAnonymous]
public class EmailTrackController : Controller
{
    // A 43-byte transparent 1×1 GIF.
    private static readonly byte[] Pixel =
        Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

    private readonly ApplicationDbContext _db;
    public EmailTrackController(ApplicationDbContext db) => _db = db;

    [HttpGet("/e/o/{token}.png")]
    public async Task<IActionResult> Open(string token)
    {
        if (Guid.TryParse(token, out var id))
        {
            try
            {
                var row = await _db.EmailLogs.FirstOrDefaultAsync(e => e.TrackId == id);
                if (row != null)
                {
                    row.OpenedAt ??= DateTime.UtcNow;   // first open wins
                    row.OpenCount++;
                    await _db.SaveChangesAsync();
                }
            }
            catch { /* tracking must never fail the pixel */ }
        }
        // Never let a mail client cache the pixel, so repeat opens are counted.
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, private";
        Response.Headers.Pragma = "no-cache";
        return File(Pixel, "image/gif");
    }
}
