using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PropertyTax.API.DTOs;
using PropertyTax.API.Models;
using PropertyTax.API.Services;

namespace PropertyTax.API.Controllers;

[ApiController]
[Route("api/payment")]
public class PaymentController : ControllerBase
{
    private readonly PaymentService _paymentService;
    private readonly ILogger<PaymentController> _logger;

    public PaymentController(PaymentService paymentService, ILogger<PaymentController> logger)
    {
        _paymentService = paymentService;
        _logger = logger;
    }

    [Authorize(Roles = SystemRoles.Admin + "," + SystemRoles.Accountant + "," + SystemRoles.Staff)]
    [HttpPost]
    public async Task<ActionResult<ApiResponse<PaymentDto>>> Record([FromBody] PaymentDto paymentDto)
    {
        try
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var roles = User.Claims.Where(c => c.Type == ClaimTypes.Role || c.Type == "role").Select(c => c.Value).ToArray();
            _logger.LogInformation("Payment.Record invoked by user {UserId} roles={Roles}", userId ?? "(anonymous)", string.Join(',', roles));
            var payment = await _paymentService.RecordAsync(paymentDto, userId);
            return Ok(ApiResponse<PaymentDto>.Ok(payment, "Payment recorded successfully."));
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(ApiResponse<PaymentDto>.Fail(exception.Message));
        }
    }

    [Authorize(Roles = SystemRoles.Admin + "," + SystemRoles.Accountant + "," + SystemRoles.Auditor + "," + SystemRoles.Staff)]
    [HttpGet("quote")]
    public async Task<ActionResult<ApiResponse<PaymentQuoteDto>>> Quote(
        [FromQuery] int propertyId,
        [FromQuery] int taxYear,
        [FromQuery] string? quarter,
        [FromQuery] DateTime? paymentDateUtc)
    {
        try
        {
            var quote = await _paymentService.GetQuoteAsync(propertyId, taxYear, quarter, paymentDateUtc);
            return Ok(ApiResponse<PaymentQuoteDto>.Ok(quote, "Payment quote generated successfully."));
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(ApiResponse<PaymentQuoteDto>.Fail(exception.Message));
        }
    }

    [Authorize(Roles = SystemRoles.Admin + "," + SystemRoles.Accountant + "," + SystemRoles.Auditor + "," + SystemRoles.Staff)]
    [HttpGet("history")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<PaymentDto>>>> History([FromQuery] int? taxpayerId, [FromQuery] int? propertyId)
    {
        var payments = await _paymentService.GetHistoryAsync(taxpayerId, propertyId);
        return Ok(ApiResponse<IReadOnlyCollection<PaymentDto>>.Ok(payments, "Payment history retrieved successfully."));
    }
}