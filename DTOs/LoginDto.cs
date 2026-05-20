using System.ComponentModel.DataAnnotations;

namespace PropertyTax.API.DTOs;

public class LoginDto
{
    [Required]
    [MaxLength(256)]
    public string Username { get; set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string Password { get; set; } = string.Empty;
}