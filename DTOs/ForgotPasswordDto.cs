using System.ComponentModel.DataAnnotations;

namespace PropertyTax.API.DTOs;

public class ForgotPasswordDto
{
    [Required]
    [EmailAddress]
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;
}