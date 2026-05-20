using System.ComponentModel.DataAnnotations;

namespace PropertyTax.API.DTOs;

public class PropertyDto
{
    public int Id { get; set; }
    public int? TaxpayerId { get; set; }
    public int? ProvinceId { get; set; }
    public int? CityMunicipalityId { get; set; }
    public int? BarangayId { get; set; }

    [Required]
    [MaxLength(150)]
    public string OwnerName { get; set; } = string.Empty;

    [EmailAddress]
    [MaxLength(256)]
    public string? OwnerEmail { get; set; }

    [MaxLength(50)]
    public string? OwnerPhone { get; set; }

    [MaxLength(255)]
    public string? OwnerAddress { get; set; }

    [MaxLength(50)]
    public string? TaxIdentificationNumber { get; set; }

    [Required]
    [MaxLength(50)]
    public string Pin { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? TaxDeclarationNumber { get; set; }

    [Required]
    [MaxLength(100)]
    public string Barangay { get; set; } = string.Empty;

    [MaxLength(100)]
    public string Municipality { get; set; } = string.Empty;

    [MaxLength(255)]
    public string Address { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string PropertyType { get; set; } = string.Empty;

    [MaxLength(100)]
    public string LotNumber { get; set; } = string.Empty;

    [Range(typeof(decimal), "0", "9999999999999999.99")]
    public decimal AreaSquareMeters { get; set; }

    [Range(typeof(decimal), "0", "9999999999999999.99")]
    public decimal MarketValue { get; set; }

    [Range(typeof(decimal), "0", "100")]
    public decimal AssessmentLevel { get; set; }

    [Range(typeof(decimal), "0", "100")]
    public decimal TaxRate { get; set; }

    [MaxLength(100)]
    public string? ZoningClassification { get; set; }

    [MaxLength(500)]
    public string? Remarks { get; set; }

    [MaxLength(50)]
    public string Status { get; set; } = "Registered";
    public DateTime? DateRegisteredUtc { get; set; }
}