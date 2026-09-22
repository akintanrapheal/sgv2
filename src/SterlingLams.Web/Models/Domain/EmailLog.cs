namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// One record per email the system attempted to send — for the admin "Email log" so staff can
/// see what went out and, crucially, which sends failed and why. Records the SMTP-level outcome
/// (accepted / failed); true inbox delivery/bounce tracking would need a provider with webhooks.
/// </summary>
public class EmailLog
{
    public int Id { get; set; }

    public string ToEmail { get; set; } = string.Empty;
    public string? ToName { get; set; }
    public string Subject { get; set; } = string.Empty;

    /// <summary>True when the send was accepted (SMTP OK, or written to the dev pickup folder).</summary>
    public bool Sent { get; set; }

    /// <summary>Failure reason when <see cref="Sent"/> is false.</summary>
    public string? Error { get; set; }

    /// <summary>"smtp" (real send), "pickup" (dev disk fallback), or "skipped" (SMTP not configured).</summary>
    public string Channel { get; set; } = "smtp";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // ── Open tracking ─────────────────────────────────────────────────────────
    /// <summary>Unique token embedded in the email's 1×1 tracking pixel; set on real sends so an open
    /// can be tied back to this row. Null for skipped sends and pre-tracking rows.</summary>
    public Guid? TrackId { get; set; }

    /// <summary>When the recipient first loaded the tracking pixel (i.e. opened the email). Null = not
    /// recorded as opened. NOTE: image-blocking mail clients never load the pixel, and some (Apple Mail
    /// Privacy, Gmail proxy) pre-fetch it — so this is a useful signal, not a guarantee.</summary>
    public DateTime? OpenedAt { get; set; }

    /// <summary>How many times the pixel has been loaded (repeat opens / forwards).</summary>
    public int OpenCount { get; set; }
}
