namespace SterlingLams.Web.Models.Domain;

public class AuditLog
{
    public int Id { get; set; }
    public string Action { get; set; } = string.Empty;       // Create | Update | Delete | Login | Export | etc.
    public string EntityType { get; set; } = string.Empty;   // Product | Store | Order | Setting | ...
    public string EntityId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string PerformedBy { get; set; } = string.Empty;  // user email
    public string? IpAddress { get; set; }                   // client IP for security auditing
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
