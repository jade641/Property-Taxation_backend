using System.ComponentModel.DataAnnotations;

namespace PropertyTax.API.DTOs;

public class UpdateProfileDto
{
    [Required]
    [MaxLength(150)]
    public string FullName { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    [EmailAddress]
    [MaxLength(256)]
    public string? BackupEmail { get; set; }

    [MaxLength(30)]
    public string? PhoneNumber { get; set; }

    [MaxLength(100)]
    public string? Position { get; set; }

    [MaxLength(120)]
    public string? Municipality { get; set; }
}
