namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Validates a chat image upload by its real file signature (magic bytes) — NOT the extension or the
/// client-supplied content-type, both of which are spoofable. Returns the safe extension + content-type
/// to store with, or null if the bytes aren't a recognised image. The caller stores the bytes RAW in R2
/// (<see cref="Services.IR2Storage.PutAsync"/>) and never decodes them with ImageSharp, so the decoder
/// advisories can't be reached from customer input.
/// </summary>
public static class ChatImageValidator
{
    public const int MaxBytes = 5 * 1024 * 1024; // 5 MB

    /// <summary>Returns (ext, contentType) for a recognised JPEG/PNG/GIF/WebP, else null.</summary>
    public static (string ext, string contentType)? Sniff(byte[] b)
    {
        if (b == null || b.Length < 12) return null;

        // JPEG: FF D8 FF
        if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
            return ("jpg", "image/jpeg");

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 &&
            b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A)
            return ("png", "image/png");

        // GIF: "GIF87a" / "GIF89a"
        if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x38)
            return ("gif", "image/gif");

        // WebP: "RIFF" .... "WEBP"
        if (b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 &&
            b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50)
            return ("webp", "image/webp");

        return null;
    }
}
