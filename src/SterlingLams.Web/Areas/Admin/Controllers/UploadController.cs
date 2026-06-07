using Microsoft.AspNetCore.Mvc;

namespace SterlingLams.Web.Areas.Admin.Controllers;

public class UploadController : AdminBaseController
{
    protected override string Section => "Upload";

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

        var folder = string.IsNullOrWhiteSpace(subfolder) ? "uploads" : $"uploads/{subfolder.Trim('/')}";
        var dir = Path.Combine(_env.WebRootPath, folder);
        Directory.CreateDirectory(dir);

        var fileName = $"{Guid.NewGuid():N}{ext.ToLowerInvariant()}";
        var fullPath = Path.Combine(dir, fileName);

        await using var stream = System.IO.File.Create(fullPath);
        await file.CopyToAsync(stream);

        var url = $"/{folder}/{fileName}";
        return Ok(new { url });
    }
}
