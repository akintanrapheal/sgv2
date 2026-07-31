using Microsoft.AspNetCore.Http;

namespace SterlingLams.Web.Services;

/// <summary>
/// The one place that decides whether an uploaded file is an acceptable image. UploadController had
/// these checks; the product and category image paths did not, so they accepted any file type and any
/// size — a .html or .svg dropped into wwwroot/uploads is served back from our own origin.
/// </summary>
public static class ImageUploadRules
{
    public const long MaxBytes = 10 * 1024 * 1024;   // 10 MB

    public static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp", ".gif" };

    /// <summary>
    /// Null when the file is an acceptable image; otherwise a message safe to show the admin.
    /// </summary>
    public static string? Validate(IFormFile? file)
    {
        if (file == null || file.Length == 0) return "No file provided.";
        if (file.Length > MaxBytes) return "File too large. Maximum 10 MB.";
        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrEmpty(ext) || !AllowedExtensions.Contains(ext))
            return "Invalid file type. Allowed: JPG, PNG, WEBP, GIF.";
        return null;
    }
}
