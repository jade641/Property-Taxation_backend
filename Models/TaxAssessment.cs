namespace PropertyTax.API.Models;

public class TaxAssessment
{
    public int Id { get; set; }
    public int PropertyId { get; set; }
    public int TaxYear { get; set; }
    public decimal MarketValue { get; set; }
    public decimal AssessmentLevel { get; set; }
    public decimal AssessedValue { get; set; }
    public decimal TaxRate { get; set; }
    public decimal TaxDue { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CalculatedByUserId { get; set; }

    public Property Property { get; set; } = null!;
}