using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PropertyTax.API.DTOs;
using PropertyTax.API.Models;
using PropertyTax.API.Services;

namespace PropertyTax.API.Controllers;

[ApiController]
[Route("api/property")]
public class PropertyController : ControllerBase
{
    private readonly PropertyService _propertyService;

    public PropertyController(PropertyService propertyService)
    {
        _propertyService = propertyService;
    }

    [Authorize(Roles = SystemRoles.Admin + "," + SystemRoles.Staff + "," + SystemRoles.Accountant + "," + SystemRoles.Auditor)]
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<PropertyDto>>>> GetAll()
    {
        var properties = await _propertyService.GetAllAsync();
        return Ok(ApiResponse<IReadOnlyCollection<PropertyDto>>.Ok(properties, "Properties retrieved successfully."));
    }

    [Authorize(Roles = SystemRoles.Admin + "," + SystemRoles.Staff + "," + SystemRoles.Accountant + "," + SystemRoles.Auditor)]
    [HttpGet("{id:int}")]
    public async Task<ActionResult<ApiResponse<PropertyDto>>> GetById(int id)
    {
        var property = await _propertyService.GetByIdAsync(id);

        if (property is null)
        {
            return NotFound(ApiResponse<PropertyDto>.Fail("Property not found."));
        }

        return Ok(ApiResponse<PropertyDto>.Ok(property, "Property retrieved successfully."));
    }

    [Authorize(Roles = SystemRoles.Admin + "," + SystemRoles.Staff)]
    [HttpPost]
    public async Task<ActionResult<ApiResponse<PropertyDto>>> Create([FromBody] PropertyDto propertyDto)
    {
        try
        {
            var property = await _propertyService.CreateAsync(propertyDto);
            return CreatedAtAction(nameof(GetById), new { id = property.Id }, ApiResponse<PropertyDto>.Ok(property, "Property registered successfully."));
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(ApiResponse<PropertyDto>.Fail(exception.Message));
        }
    }

    [Authorize(Roles = SystemRoles.Admin + "," + SystemRoles.Staff)]
    [HttpPost("register")]
    public async Task<ActionResult<ApiResponse<PropertyDto>>> Register([FromBody] RegisterPropertyDto propertyDto)
    {
        try
        {
            var property = await _propertyService.RegisterAsync(propertyDto);
            return CreatedAtAction(nameof(GetById), new { id = property.Id }, ApiResponse<PropertyDto>.Ok(property, "Property registration submitted successfully."));
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(ApiResponse<PropertyDto>.Fail(exception.Message));
        }
    }

    [Authorize(Roles = SystemRoles.Admin + "," + SystemRoles.Staff)]
    [HttpPut("{id:int}")]
    public async Task<ActionResult<ApiResponse<PropertyDto>>> Update(int id, [FromBody] PropertyDto propertyDto)
    {
        try
        {
            var property = await _propertyService.UpdateAsync(id, propertyDto);

            if (property is null)
            {
                return NotFound(ApiResponse<PropertyDto>.Fail("Property not found."));
            }

            return Ok(ApiResponse<PropertyDto>.Ok(property, "Property updated successfully."));
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(ApiResponse<PropertyDto>.Fail(exception.Message));
        }
    }

    [Authorize(Roles = SystemRoles.Admin)]
    [HttpDelete("{id:int}")]
    public async Task<ActionResult<ApiResponse<object?>>> Delete(int id)
    {
        var deleted = await _propertyService.DeleteAsync(id);

        if (!deleted)
        {
            return NotFound(ApiResponse<object?>.Fail("Property not found."));
        }

        return Ok(ApiResponse<object?>.Ok(null, "Property deleted successfully."));
    }
}