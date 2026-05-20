using System.ComponentModel.DataAnnotations;

namespace PropertyTax.API.DTOs;

public class FilingUploadDto
{
    [Required]
    [Range(1, int.MaxValue)]
    public int PropertyId { get; set; }

    [Required]
    public IFormFile File { get; set; } = null!;

    [MaxLength(100)]
    public string Folder { get; set; } = "Property Documents";
}