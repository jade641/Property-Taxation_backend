using System.ComponentModel.DataAnnotations;

namespace PropertyTax.API.DTOs;

public class RegisterDto
{
    [Required]
    [MaxLength(100)]
    public string Username { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MinLength(8)]
    [MaxLength(128)]
    public string Password { get; set; } = string.Empty;

    [Required]
    [MaxLength(150)]
    public string FullName { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string Role { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}