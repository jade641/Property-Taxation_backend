namespace PropertyTax.API.DTOs;

public sealed record ProvinceDto(int Id, string PsgcCode, string Name);

public sealed record CityMunicipalityDto(int Id, int ProvinceId, string PsgcCode, string Name, string LguType);

public sealed record BarangayDto(
    int Id,
    int CityMunicipalityId,
    string PsgcCode,
    string Name,
    string CityMunicipalityName,
    string ProvinceName);