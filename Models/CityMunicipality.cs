namespace PropertyTax.API.Models;

public class CityMunicipality
{
    public int Id { get; set; }
    public int ProvinceId { get; set; }
    public string PsgcCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string LguType { get; set; } = string.Empty;

    public Province Province { get; set; } = null!;
    public ICollection<Barangay> Barangays { get; set; } = new List<Barangay>();
}