using System.ComponentModel.DataAnnotations;

namespace PropertyTax.API.DTOs;

public class ResetPasswordDto
{
    [Required]
    [EmailAddress]
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [RegularExpression(@"^\d{6}$")]
    [MaxLength(6)]
    public string Otp { get; set; } = string.Empty;

    [Required]
    [MinLength(8)]
    [MaxLength(128)]
    public string NewPassword { get; set; } = string.Empty;

    [Required]
    [Compare(nameof(NewPassword), ErrorMessage = "Passwords do not match.")]
    [MaxLength(128)]
    public string ConfirmPassword { get; set; } = string.Empty;
}
