namespace PropertyTax.API.Models;

public class Taxpayer
{
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }
    public string? Address { get; set; }
    public string? TaxIdentificationNumber { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<Property> Properties { get; set; } = new List<Property>();
    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
}