using System.ComponentModel.DataAnnotations;

namespace PropertyTax.API.DTOs;

public class PaymentQuoteDto
{
    [Range(1, int.MaxValue)]
    public int PropertyId { get; set; }

    public string PropertyPin { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;

    [Range(1900, 2200)]
    public int TaxYear { get; set; }

    [MaxLength(50)]
    public string Quarter { get; set; } = "Annual";

    public DateTime PaymentDateUtc { get; set; }
    public DateTime DueDateUtc { get; set; }

    [Range(typeof(decimal), "0", "9999999999999999.99")]
    public decimal AnnualTaxDue { get; set; }

    [Range(typeof(decimal), "0", "9999999999999999.99")]
    public decimal TotalPaidToDate { get; set; }

    [Range(typeof(decimal), "0", "9999999999999999.99")]
    public decimal OutstandingPrincipal { get; set; }

    [Range(typeof(decimal), "0", "9999999999999999.99")]
    public decimal QuarterDue { get; set; }

    [Range(typeof(decimal), "0", "9999999999999999.99")]
    public decimal Penalty { get; set; }

    [Range(typeof(decimal), "0", "9999999999999999.99")]
    public decimal PayableAmount { get; set; }

    [MaxLength(50)]
    public string Status { get; set; } = "Unpaid";
}