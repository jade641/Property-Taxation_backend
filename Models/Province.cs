namespace PropertyTax.API.Models;

public class Province
{
    public int Id { get; set; }
    public string RegionCode { get; set; } = "110000000";
    public string PsgcCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    public ICollection<CityMunicipality> CitiesMunicipalities { get; set; } = new List<CityMunicipality>();
}