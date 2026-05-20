using System.ComponentModel.DataAnnotations;

namespace PropertyTax.API.DTOs;

public class UpdateUserDto
{
    [Required]
    [MaxLength(100)]
    public string Username { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    [EmailAddress]
    [MaxLength(256)]
    public string? BackupEmail { get; set; }

    [Required]
    [MaxLength(150)]
    public string FullName { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string Role { get; set; } = string.Empty;

    [MinLength(8)]
    [MaxLength(128)]
    public string? Password { get; set; }

    public bool IsActive { get; set; } = true;
}