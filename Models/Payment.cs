namespace PropertyTax.API.Models;

public class Payment
{
    public int Id { get; set; }
    public int PropertyId { get; set; }
    public int TaxpayerId { get; set; }
    public int TaxYear { get; set; }
    public string Quarter { get; set; } = "Annual";
    public decimal AmountDue { get; set; }
    public decimal AmountPaid { get; set; }
    public string PaymentMethod { get; set; } = string.Empty;
    public string? ReferenceNumber { get; set; }
    public string? BankName { get; set; }
    public string OfficialReceiptNumber { get; set; } = string.Empty;
    public DateTime PaymentDateUtc { get; set; } = DateTime.UtcNow;
    public DateTime DueDateUtc { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "Paid";
    public decimal Penalty { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? RecordedByUserId { get; set; }

    public Property Property { get; set; } = null!;
    public Taxpayer Taxpayer { get; set; } = null!;
}