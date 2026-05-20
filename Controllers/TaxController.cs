using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PropertyTax.API.DTOs;
using PropertyTax.API.Models;
using PropertyTax.API.Services;

namespace PropertyTax.API.Controllers;

[ApiController]
[Route("api/tax")]
public class TaxController : ControllerBase
{
    private readonly TaxService _taxService;

    public TaxController(TaxService taxService)
    {
        _taxService = taxService;
    }

    [Authorize(Roles = SystemRoles.Admin + "," + SystemRoles.Staff)]
    [HttpPost("calculate")]
    public async Task<ActionResult<ApiResponse<TaxDto>>> Calculate([FromBody] TaxDto taxDto)
    {
        try
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var assessment = await _taxService.CalculateAsync(taxDto, userId);
            return Ok(ApiResponse<TaxDto>.Ok(assessment, "Tax assessment computed successfully."));
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(ApiResponse<TaxDto>.Fail(exception.Message));
        }
    }

    [Authorize(Roles = SystemRoles.Admin + "," + SystemRoles.Staff + "," + SystemRoles.Accountant + "," + SystemRoles.Auditor)]
    [HttpGet("assessments")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<TaxDto>>>> GetAssessments()
    {
        var assessments = await _taxService.GetAssessmentsAsync();
        return Ok(ApiResponse<IReadOnlyCollection<TaxDto>>.Ok(assessments, "Tax assessments retrieved successfully."));
    }
}