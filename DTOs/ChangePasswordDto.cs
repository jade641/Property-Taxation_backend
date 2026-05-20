using System.ComponentModel.DataAnnotations;

namespace PropertyTax.API.DTOs;

public class ChangePasswordDto
{
    [Required]
    [MaxLength(128)]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required]
    [MinLength(8)]
    [MaxLength(128)]
    public string NewPassword { get; set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    [Compare(nameof(NewPassword), ErrorMessage = "New password and confirmation password do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;
}