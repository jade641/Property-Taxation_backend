using System.ComponentModel.DataAnnotations;

namespace PropertyTax.API.DTOs;

public class PaymentDto
{
    public int Id { get; set; }

    [Required]
    [Range(1, int.MaxValue)]
    public int PropertyId { get; set; }

    [Range(1, int.MaxValue)]
    public int? TaxpayerId { get; set; }
    public string? PropertyPin { get; set; }
    public string? OwnerName { get; set; }
    public string? Barangay { get; set; }

    [Range(1900, 2200)]
    public int TaxYear { get; set; } = DateTime.UtcNow.Year;

    [MaxLength(50)]
    public string Quarter { get; set; } = "Annual";

    [Range(typeof(decimal), "0", "9999999999999999.99")]
    public decimal AmountDue { get; set; }

    [Range(typeof(decimal), "0", "9999999999999999.99")]
    public decimal AmountPaid { get; set; }

    [MaxLength(50)]
    public string PaymentMethod { get; set; } = "Cash";

    [MaxLength(100)]
    public string? ReferenceNumber { get; set; }

    [MaxLength(120)]
    public string? BankName { get; set; }

    public DateTime? PaymentDateUtc { get; set; }
    public DateTime? DueDateUtc { get; set; }

    [MaxLength(50)]
    public string Status { get; set; } = "Paid";

    [Range(typeof(decimal), "0", "9999999999999999.99")]
    public decimal Penalty { get; set; }

    [MaxLength(100)]
    public string? OfficialReceiptNumber { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    [Range(typeof(decimal), "0", "9999999999999999.99")]
    public decimal? RemainingBalance { get; set; }
}