using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PropertyTax.API.Data;
using PropertyTax.API.DTOs;
using PropertyTax.API.Models;
using PropertyTax.API.Models.Enums;

namespace PropertyTax.API.Controllers;

[ApiController]
[Authorize(Roles = SystemRoles.Admin + "," + SystemRoles.Accountant + "," + SystemRoles.Auditor)]
[Route("api/compliance")]
public class ComplianceController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public ComplianceController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet("delinquent")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<ComplianceStatusItem>>>> GetDelinquent()
    {
        var statuses = await BuildComplianceStatusAsync();
        var delinquent = statuses.Where(item => item.RemainingBalance > 0m).ToList();
        return Ok(ApiResponse<IReadOnlyCollection<ComplianceStatusItem>>.Ok(delinquent, "Delinquent properties retrieved successfully."));
    }

    [HttpGet("status")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<ComplianceStatusItem>>>> GetStatus()
    {
        var statuses = await BuildComplianceStatusAsync();
        return Ok(ApiResponse<IReadOnlyCollection<ComplianceStatusItem>>.Ok(statuses, "Compliance status retrieved successfully."));
    }

    private async Task<List<ComplianceStatusItem>> BuildComplianceStatusAsync()
    {
        var assessments = await _dbContext.TaxAssessments
            .AsNoTracking()
            .Include(assessment => assessment.Property)
            .ThenInclude(property => property.Taxpayer)
            .ToListAsync();

        var validAssessments = assessments
            .Where(assessment => assessment.Property is not null && assessment.Property.Taxpayer is not null)
            .ToList();

        var paymentLookup = await _dbContext.Payments
            .AsNoTracking()
            .GroupBy(payment => new { payment.PropertyId, payment.TaxYear })
            .Select(group => new
            {
                group.Key.PropertyId,
                group.Key.TaxYear,
                TotalPaid = group.Sum(payment => payment.AmountPaid),
                LastPaymentDateUtc = group.Max(payment => payment.PaymentDateUtc),
                HasLatePayment = group.Any(payment => payment.Status == "Late"),
            })
            .ToDictionaryAsync(item => (item.PropertyId, item.TaxYear));

        return validAssessments
            .Select(assessment =>
            {
                paymentLookup.TryGetValue((assessment.PropertyId, assessment.TaxYear), out var paymentInfo);
                var totalPaid = paymentInfo?.TotalPaid ?? 0m;
                var remainingBalance = Math.Max(assessment.TaxDue - totalPaid, 0m);
                var status = paymentInfo?.HasLatePayment == true
                    ? ComplianceStatus.Late.ToString()
                    : remainingBalance <= 0m
                        ? ComplianceStatus.Compliant.ToString()
                        : totalPaid > 0m
                            ? ComplianceStatus.Unpaid.ToString()
                            : ComplianceStatus.Unpaid.ToString();

                return new ComplianceStatusItem(
                    assessment.PropertyId,
                    assessment.Property!.Pin,
                    assessment.Property.Taxpayer!.FullName,
                    assessment.Property.Barangay,
                    assessment.Property.PropertyType,
                    assessment.TaxYear,
                    assessment.TaxDue,
                    totalPaid,
                    remainingBalance,
                    status,
                    paymentInfo?.LastPaymentDateUtc);
            })
            .OrderByDescending(item => item.TaxYear)
            .ThenBy(item => item.OwnerName)
            .ToList();
    }

    public sealed record ComplianceStatusItem(
        int PropertyId,
        string PropertyPin,
        string OwnerName,
        string Barangay,
        string PropertyType,
        int TaxYear,
        decimal TotalDue,
        decimal TotalPaid,
        decimal RemainingBalance,
        string Status,
        DateTime? LastPaymentDateUtc);
}