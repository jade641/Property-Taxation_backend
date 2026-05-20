using System.ComponentModel.DataAnnotations;

namespace PropertyTax.API.DTOs;

public class VerifyOtpRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [RegularExpression(@"^\d{6}$")]
    public string Otp { get; set; } = string.Empty;
}
