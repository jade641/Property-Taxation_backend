namespace PropertyTax.API.DTOs;

public class ProfileDto
{
    public string Id { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    // Optional secondary or recovery email address
    public string? BackupEmail { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string? Position { get; set; }
    public string? Municipality { get; set; }
}
