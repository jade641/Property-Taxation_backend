using System.ComponentModel.DataAnnotations;

namespace PropertyTax.API.DTOs;

public class RegisterPropertyDto
{
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

    [Range(1, int.MaxValue)]
    public int ProvinceId { get; set; }

    [Range(1, int.MaxValue)]
    public int CityMunicipalityId { get; set; }

    [Range(1, int.MaxValue)]
    public int BarangayId { get; set; }

    [Required]
    [MaxLength(50)]
    public string PropertyType { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string LotNumber { get; set; } = string.Empty;

    [Range(typeof(decimal), "0.01", "9999999999999999.99")]
    public decimal AreaSquareMeters { get; set; }

    [Range(typeof(decimal), "0.01", "9999999999999999.99")]
    public decimal MarketValue { get; set; }

    [MaxLength(50)]
    public string? TaxDeclarationNumber { get; set; }

    [MaxLength(100)]
    public string? ZoningClassification { get; set; }

    [MaxLength(500)]
    public string? Remarks { get; set; }
}