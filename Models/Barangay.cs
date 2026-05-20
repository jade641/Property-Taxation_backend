namespace PropertyTax.API.Models;

public class Barangay
{
    public int Id { get; set; }
    public int CityMunicipalityId { get; set; }
    public string PsgcCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    public CityMunicipality CityMunicipality { get; set; } = null!;
    public ICollection<Property> Properties { get; set; } = new List<Property>();
}