using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PropertyTax.API.Data;
using PropertyTax.API.DTOs;

namespace PropertyTax.API.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/public")]
public class PublicController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public PublicController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet("landing-summary")]
    public async Task<ActionResult<ApiResponse<LandingSummaryDto>>> GetLandingSummary()
    {
        var totalUsers = await _dbContext.Users.AsNoTracking().CountAsync();
        var activeUsers = await _dbContext.Users.AsNoTracking().CountAsync(user => user.IsActive);
        var totalProperties = await _dbContext.Properties.AsNoTracking().CountAsync();
        var totalTaxpayers = await _dbContext.Taxpayers.AsNoTracking().CountAsync();
        var totalAssessments = await _dbContext.TaxAssessments.AsNoTracking().CountAsync();

        var paymentSummary = await _dbContext.Payments
            .AsNoTracking()
            .GroupBy(payment => 1)
            .Select(group => new
            {
                TotalPayments = group.Count(),
                TotalCollected = group.Sum(payment => payment.AmountPaid),
            })
            .FirstOrDefaultAsync();

        var collectionPeriods = await _dbContext.Payments
            .AsNoTracking()
            .GroupBy(payment => new { payment.PaymentDateUtc.Year, payment.PaymentDateUtc.Month })
            .Select(group => new
            {
                group.Key.Year,
                group.Key.Month,
                TotalCollected = group.Sum(payment => payment.AmountPaid),
            })
            .OrderByDescending(item => item.Year)
            .ThenByDescending(item => item.Month)
            .ToListAsync();

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

        var compliantCount = 0;
        var lateCount = 0;
        var unpaidCount = 0;
        decimal totalOutstandingBalance = 0m;

        var assessmentTotals = await _dbContext.TaxAssessments
            .AsNoTracking()
            .Select(assessment => new
            {
                assessment.PropertyId,
                assessment.TaxYear,
                assessment.TaxDue,
            })
            .ToListAsync();

        foreach (var assessment in assessmentTotals)
        {
            paymentLookup.TryGetValue((assessment.PropertyId, assessment.TaxYear), out var totalPaid);
            var remainingBalance = Math.Max(assessment.TaxDue - totalPaid, 0m);

            totalOutstandingBalance += remainingBalance;

            if (remainingBalance <= 0m)
            {
                compliantCount += 1;
            }
            else if (totalPaid > 0m)
            {
                lateCount += 1;
            }
            else
            {
                unpaidCount += 1;
            }
        }

        var complianceRate = assessmentTotals.Count > 0
            ? Math.Round((decimal)compliantCount / assessmentTotals.Count * 100m, 1)
            : 0m;

        var latestCollectionLabel = collectionPeriods.Count > 0
            ? $"{collectionPeriods[0].Year}-{collectionPeriods[0].Month:00}"
            : null;

        var summary = new LandingSummaryDto
        {
            TotalUsers = totalUsers,
            ActiveUsers = activeUsers,
            TotalProperties = totalProperties,
            TotalTaxpayers = totalTaxpayers,
            TotalAssessments = totalAssessments,
            TotalPayments = paymentSummary?.TotalPayments ?? 0,
            TotalCollected = paymentSummary?.TotalCollected ?? 0m,
            TotalOutstandingBalance = totalOutstandingBalance,
            CompliantCount = compliantCount,
            LateCount = lateCount,
            UnpaidCount = unpaidCount,
            ComplianceRate = complianceRate,
            LatestCollectionLabel = latestCollectionLabel,
        };

        return Ok(ApiResponse<LandingSummaryDto>.Ok(summary, "Landing summary generated successfully."));
    }
}