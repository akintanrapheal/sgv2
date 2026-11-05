using System.ComponentModel.DataAnnotations;

namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// An image uploaded in the Marketing Hub email builder and kept in a reusable library (the
/// "Asset Manager"). Each upload is stored through <see cref="Services.IImageStorageService"/>
/// (R2 → Cloudinary → disk) and a row recorded here so the same image can be picked again in any
/// later campaign without re-uploading.
/// </summary>
public class MarketingAsset
{
    public int Id { get; set; }

    /// <summary>Public URL of the stored image.</summary>
    [MaxLength(1000)]
    public string Url { get; set; } = string.Empty;

    /// <summary>Original file name, shown as the caption under the thumbnail.</summary>
    [MaxLength(260)]
    public string? FileName { get; set; }

    public long SizeBytes { get; set; }

    [MaxLength(100)]
    public string? ContentType { get; set; }

    /// <summary>User id of whoever uploaded it (for audit; not shown).</summary>
    [MaxLength(450)]
    public string? CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
