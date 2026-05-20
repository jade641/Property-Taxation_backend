namespace PropertyTax.API.Models;

public class AuditLog
{
    public int Id { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityName { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public string? PerformedByUserId { get; set; }
    public string? PerformedByUsername { get; set; }
    public string? UserRole { get; set; }
    public string? IpAddress { get; set; }
    public string? Description { get; set; }
    public bool Succeeded { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}