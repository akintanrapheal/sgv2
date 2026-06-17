using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.AspNetCore.Mvc;

namespace SterlingLams.Web.Areas.Admin.Controllers;

public class UploadController : AdminBaseController
{
    // Gated on "Settings" — the screens that post here (Settings image picker, product/category images).
    protected override string Section => "Settings";

    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;

    private static readonly HashSet<string> _allowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".gif" };

    public UploadController(IWebHostEnvironment env, IConfiguration config)
    {
        _env = env;
        _config = config;
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Image(IFormFile file, string? subfolder)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file provided." });
        if (file.Length > 10 * 1024 * 1024)
            return BadRequest(new { error = "File too large. Maximum 10 MB." });

        var ext = Path.GetExtension(file.FileName);
        if (!_allowedExtensions.Contains(ext))
            return BadRequest(new { error = "Invalid file type. Allowed: JPG, PNG, WEBP, GIF." });

        // Sanitise the subfolder (reused for the Cloudinary folder and the local path).
        var safeSubfolder = "";
        if (!string.IsNullOrWhiteSpace(subfolder))
        {
            safeSubfolder = subfolder.Trim('/', '\\');
            if (safeSubfolder.Split('/', '\\').Any(seg => seg is ".." or "."))
                return BadRequest(new { error = "Invalid subfolder." });
        }

        // ── Cloudinary (persistent + CDN) when configured. Required on ephemeral hosts like Render,
        //    where local disk is wiped on every redeploy/restart. ──
        var cloudName = _config["Cloudinary:CloudName"];
        var apiKey    = _config["Cloudinary:ApiKey"];
        var apiSecret = _config["Cloudinary:ApiSecret"];
        if (!string.IsNullOrWhiteSpace(cloudName) && !string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(apiSecret))
        {
            var cloudinary = new Cloudinary(new Account(cloudName, apiKey, apiSecret)) { Api = { Secure = true } };
            await using var s = file.OpenReadStream();
            var folder = string.IsNullOrEmpty(safeSubfolder) ? "sterlinglams" : $"sterlinglams/{safeSubfolder}";
            var result = await cloudinary.UploadAsync(new ImageUploadParams
            {
                File = new FileDescription(file.FileName, s),
                Folder = folder,
                PublicId = Guid.NewGuid().ToString("N"),
                UniqueFilename = false,
                Overwrite = false
            });
            if (result.StatusCode != System.Net.HttpStatusCode.OK || result.SecureUrl == null)
                return BadRequest(new { error = "Image upload failed. Please try again." });
            return Ok(new { url = result.SecureUrl.ToString() });
        }

        // ── Fallback: local disk (development only — NOT persistent on Render). ──
        var uploadsRoot = Path.GetFullPath(Path.Combine(_env.WebRootPath, "uploads"));
        var folderPath = string.IsNullOrEmpty(safeSubfolder) ? "uploads" : $"uploads/{safeSubfolder}";
        var dir = Path.GetFullPath(Path.Combine(_env.WebRootPath, folderPath));
        if (!dir.Equals(uploadsRoot, StringComparison.OrdinalIgnoreCase) &&
            !dir.StartsWith(uploadsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Invalid subfolder." });

        Directory.CreateDirectory(dir);
        var fileName = $"{Guid.NewGuid():N}{ext.ToLowerInvariant()}";
        await using var stream = System.IO.File.Create(Path.Combine(dir, fileName));
        await file.CopyToAsync(stream);
        return Ok(new { url = $"/{folderPath}/{fileName}" });
    }
}
