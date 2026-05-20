using System.ComponentModel.DataAnnotations;

namespace PropertyTax.API.DTOs;

public class TaxDto
{
    public int Id { get; set; }

    [Required]
    [Range(1, int.MaxValue)]
    public int PropertyId { get; set; }

    public string? PropertyPin { get; set; }
    public string? OwnerName { get; set; }

    [Range(1900, 2200)]
    public int TaxYear { get; set; } = DateTime.UtcNow.Year;

    [Range(typeof(decimal), "0", "9999999999999999.99")]
    public decimal? MarketValue { get; set; }

    [Range(typeof(decimal), "0", "100")]
    public decimal? AssessmentLevel { get; set; }

    [Range(typeof(decimal), "0", "100")]
    public decimal? TaxRate { get; set; }

    [Range(typeof(decimal), "0", "9999999999999999.99")]
    public decimal AssessedValue { get; set; }

    [Range(typeof(decimal), "0", "9999999999999999.99")]
    public decimal TaxDue { get; set; }
    public DateTime? CreatedAtUtc { get; set; }
}