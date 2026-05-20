using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PropertyTax.API.DTOs;
using PropertyTax.API.Models;
using PropertyTax.API.Services;

namespace PropertyTax.API.Controllers;

[ApiController]
[Authorize(Roles = SystemRoles.Admin + "," + SystemRoles.Staff + "," + SystemRoles.Accountant + "," + SystemRoles.Auditor)]
[Route("api/location")]
public class LocationController : ControllerBase
{
    private readonly LocationService _locationService;

    public LocationController(LocationService locationService)
    {
        _locationService = locationService;
    }

    [HttpGet("provinces")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<ProvinceDto>>>> GetProvinces()
    {
        var provinces = await _locationService.GetProvincesAsync();
        return Ok(ApiResponse<IReadOnlyCollection<ProvinceDto>>.Ok(provinces, "Provinces retrieved successfully."));
    }

    [HttpGet("cities/{provinceId:int}")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<CityMunicipalityDto>>>> GetCities(int provinceId)
    {
        var cities = await _locationService.GetCitiesAsync(provinceId);
        return Ok(ApiResponse<IReadOnlyCollection<CityMunicipalityDto>>.Ok(cities, "Cities and municipalities retrieved successfully."));
    }

    [HttpGet("barangays")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<BarangayDto>>>> SearchBarangays([FromQuery] int cityId, [FromQuery] string? search)
    {
        if (cityId <= 0)
        {
            return BadRequest(ApiResponse<IReadOnlyCollection<BarangayDto>>.Fail("A valid city or municipality is required."));
        }

        var barangays = await _locationService.SearchBarangaysAsync(cityId, search);
        return Ok(ApiResponse<IReadOnlyCollection<BarangayDto>>.Ok(barangays, "Barangays retrieved successfully."));
    }
}