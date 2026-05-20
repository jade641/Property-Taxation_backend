namespace PropertyTax.API.Models;

public class Property
{
    public int Id { get; set; }
    public int TaxpayerId { get; set; }
    public int? BarangayId { get; set; }
    public string Pin { get; set; } = string.Empty;
    public string? TaxDeclarationNumber { get; set; }
    public string Barangay { get; set; } = string.Empty;
    public string Municipality { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string PropertyType { get; set; } = string.Empty;
    public string LotNumber { get; set; } = string.Empty;
    public decimal AreaSquareMeters { get; set; }
    public decimal MarketValue { get; set; }
    public decimal AssessmentLevel { get; set; }
    public decimal TaxRate { get; set; }
    public string? ZoningClassification { get; set; }
    public string? Remarks { get; set; }
    public string Status { get; set; } = "Registered";
    public DateTime DateRegisteredUtc { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }

    public Taxpayer Taxpayer { get; set; } = null!;
    public Barangay? BarangayLocation { get; set; }
    public ICollection<TaxAssessment> TaxAssessments { get; set; } = new List<TaxAssessment>();
    public ICollection<MlPrediction> MlPredictions { get; set; } = new List<MlPrediction>();
    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
    public ICollection<PropertyDocument> Documents { get; set; } = new List<PropertyDocument>();
}