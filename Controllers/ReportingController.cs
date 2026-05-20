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
[Route("api/report")]
public class ReportingController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public ReportingController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet("collections")]
    public async Task<ActionResult<ApiResponse<object>>> GetCollections()
    {
        var collectionData = await _dbContext.Payments
            .AsNoTracking()
            .GroupBy(payment => new { payment.PaymentDateUtc.Year, payment.PaymentDateUtc.Month })
            .Select(group => new
            {
                group.Key.Year,
                group.Key.Month,
                TotalCollected = group.Sum(payment => payment.AmountPaid),
                PaymentCount = group.Count(),
            })
            .OrderBy(item => item.Year)
            .ThenBy(item => item.Month)
            .ToListAsync();

        var assessments = await _dbContext.TaxAssessments
            .AsNoTracking()
            .Include(assessment => assessment.Property)
            .ToListAsync();

        var validAssessments = assessments
            .Where(assessment => assessment.Property is not null)
            .ToList();

        var paymentLookup = await _dbContext.Payments
            .AsNoTracking()
            .GroupBy(payment => new { payment.PropertyId, payment.TaxYear })
            .Select(group => new
            {
                group.Key.PropertyId,
                group.Key.TaxYear,
                TotalPaid = group.Sum(payment => payment.AmountPaid),
            })
            .ToDictionaryAsync(item => (item.PropertyId, item.TaxYear), item => item.TotalPaid);

        var byBarangay = validAssessments
            .GroupBy(assessment => assessment.Property?.Barangay ?? "Unspecified")
            .Select(group => new
            {
                barangay = group.Key,
                totalDue = group.Sum(assessment => assessment.TaxDue),
                totalCollected = group.Sum(assessment => paymentLookup.TryGetValue((assessment.PropertyId, assessment.TaxYear), out var totalPaid) ? totalPaid : 0m),
            })
            .OrderBy(item => item.barangay)
            .ToList();

        var response = new
        {
            labels = collectionData.Select(item => $"{item.Year}-{item.Month:00}"),
            datasets = new[]
            {
                new
                {
                    label = "Collections",
                    data = collectionData.Select(item => item.TotalCollected),
                },
            },
            summary = new
            {
                totalDue = assessments.Sum(assessment => assessment.TaxDue),
                totalCollected = collectionData.Sum(item => item.TotalCollected),
                totalPayments = collectionData.Sum(item => item.PaymentCount),
            },
            byBarangay,
        };

        return Ok(ApiResponse<object>.Ok(response, "Collection report generated successfully."));
    }

    [HttpGet("delinquency")]
    public async Task<ActionResult<ApiResponse<object>>> GetDelinquency()
    {
        var assessments = await _dbContext.TaxAssessments
            .AsNoTracking()
            .Include(assessment => assessment.Property)
            .ToListAsync();

        var validAssessments = assessments
            .Where(assessment => assessment.Property is not null)
            .ToList();

        var paymentLookup = await _dbContext.Payments
            .AsNoTracking()
            .GroupBy(payment => new { payment.PropertyId, payment.TaxYear })
            .Select(group => new
            {
                group.Key.PropertyId,
                group.Key.TaxYear,
                TotalPaid = group.Sum(payment => payment.AmountPaid),
            })
            .ToDictionaryAsync(item => (item.PropertyId, item.TaxYear));

            var delinquencyRows = validAssessments.Select(assessment =>
        {
            paymentLookup.TryGetValue((assessment.PropertyId, assessment.TaxYear), out var paymentInfo);
            var totalPaid = paymentInfo?.TotalPaid ?? 0m;
            var remainingBalance = Math.Max(assessment.TaxDue - totalPaid, 0m);
            var status = remainingBalance <= 0m
                ? ComplianceStatus.Compliant.ToString()
                : totalPaid > 0m
                    ? ComplianceStatus.Late.ToString()
                    : ComplianceStatus.Unpaid.ToString();

            return new
            {
                Barangay = assessment.Property?.Barangay ?? "Unspecified",
                TotalDue = assessment.TaxDue,
                TotalPaid = totalPaid,
                Status = status,
                RemainingBalance = remainingBalance,
            };
        }).ToList();

        var response = new
        {
            labels = new[] { "Compliant", "Late", "Unpaid" },
            datasets = new[]
            {
                new
                {
                    label = "Properties",
                    data = new[]
                    {
                        delinquencyRows.Count(item => item.Status == "Compliant"),
                        delinquencyRows.Count(item => item.Status == "Late"),
                        delinquencyRows.Count(item => item.Status == "Unpaid"),
                    },
                },
            },
            byBarangay = delinquencyRows
                .GroupBy(item => item.Barangay)
                .Select(group => new
                {
                    barangay = group.Key,
                    compliant = group.Count(item => item.Status == "Compliant"),
                    late = group.Count(item => item.Status == "Late"),
                    unpaid = group.Count(item => item.Status == "Unpaid"),
                    outstandingBalance = group.Sum(item => item.RemainingBalance),
                })
                .OrderBy(item => item.barangay),
            summary = new
            {
                totalDue = assessments.Sum(assessment => assessment.TaxDue),
                totalPaid = delinquencyRows.Sum(item => item.TotalPaid),
                outstandingBalance = delinquencyRows.Sum(item => item.RemainingBalance),
                compliantCount = delinquencyRows.Count(item => item.Status == "Compliant"),
                lateCount = delinquencyRows.Count(item => item.Status == "Late"),
                unpaidCount = delinquencyRows.Count(item => item.Status == "Unpaid"),
            },
        };

        return Ok(ApiResponse<object>.Ok(response, "Delinquency report generated successfully."));
    }

    [HttpGet("properties")]
    public async Task<ActionResult<ApiResponse<object>>> GetProperties()
    {
        var properties = await _dbContext.Properties
            .AsNoTracking()
            .ToListAsync();

        var response = new
        {
            byType = properties
                .GroupBy(property => property.PropertyType)
                .Select(group => new
                {
                    type = group.Key,
                    count = group.Count(),
                    totalMarketValue = group.Sum(property => property.MarketValue),
                })
                .OrderBy(item => item.type),
            byBarangay = properties
                .GroupBy(property => property.Barangay)
                .Select(group => new
                {
                    barangay = group.Key,
                    count = group.Count(),
                    totalMarketValue = group.Sum(property => property.MarketValue),
                })
                .OrderBy(item => item.barangay),
            summary = new
            {
                totalProperties = properties.Count,
                totalMarketValue = properties.Sum(property => property.MarketValue),
                totalAssessedValue = properties.Sum(property => property.MarketValue * (property.AssessmentLevel / 100m)),
            },
        };

        return Ok(ApiResponse<object>.Ok(response, "Property report generated successfully."));
    }
}