using Microsoft.AspNetCore.Mvc;

namespace SterlingLams.Web.Areas.Admin.Controllers;

public class UploadController : AdminBaseController
{
    // Gated on "Settings" — the only screen that posts here (Settings/Index image picker).
    // It previously declared a phantom "Upload" section that isn't in AdminSections.All, so no
    // staff role could ever be granted it and only full Admins could upload (broken for any role
    // granted Settings).
    protected override string Section => "Settings";

    private readonly IWebHostEnvironment _env;

    private static readonly HashSet<string> _allowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".gif" };

    public UploadController(IWebHostEnvironment env) => _env = env;

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

        var uploadsRoot = Path.GetFullPath(Path.Combine(_env.WebRootPath, "uploads"));
        var folder = "uploads";
        if (!string.IsNullOrWhiteSpace(subfolder))
        {
            var safeSubfolder = subfolder.Trim('/', '\\');
            if (safeSubfolder.Split('/', '\\').Any(seg => seg == ".." || seg == "."))
                return BadRequest(new { error = "Invalid subfolder." });
            folder = $"uploads/{safeSubfolder}";
        }

        var dir = Path.GetFullPath(Path.Combine(_env.WebRootPath, folder));
        if (!dir.Equals(uploadsRoot, StringComparison.OrdinalIgnoreCase) &&
            !dir.StartsWith(uploadsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Invalid subfolder." });

        Directory.CreateDirectory(dir);

        var fileName = $"{Guid.NewGuid():N}{ext.ToLowerInvariant()}";
        var fullPath = Path.Combine(dir, fileName);

        await using var stream = System.IO.File.Create(fullPath);
        await file.CopyToAsync(stream);

        var url = $"/{folder}/{fileName}";
        return Ok(new { url });
    }
}
