using Microsoft.AspNetCore.Mvc;

namespace SterlingLams.Web.Areas.Admin.Controllers;

public class UploadController : AdminBaseController
{
    // Gated on "Settings" — the screens that post here (Settings image picker, product/category images).
    protected override string Section => "Settings";

    private readonly SterlingLams.Web.Services.IImageStorageService _imageStorage;

    public UploadController(SterlingLams.Web.Services.IImageStorageService imageStorage)
    {
        _imageStorage = imageStorage;
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Image(IFormFile file, string? subfolder)
    {
        // Shared with the product/category image paths — see ImageUploadRules.
        var invalid = SterlingLams.Web.Services.ImageUploadRules.Validate(file);
        if (invalid != null) return BadRequest(new { error = invalid });

        // Sanitise the subfolder.
        var safeSubfolder = "";
        if (!string.IsNullOrWhiteSpace(subfolder))
        {
            safeSubfolder = subfolder.Trim('/', '\\');
            if (safeSubfolder.Split('/', '\\').Any(seg => seg is ".." or "."))
                return BadRequest(new { error = "Invalid subfolder." });
        }

        // R2 → Cloudinary → local disk, all handled by the shared storage service.
        var url = await _imageStorage.UploadAsync(file, safeSubfolder);
        if (url == null) return BadRequest(new { error = "Image upload failed. Please try again." });
        return Ok(new { url });
    }
}
