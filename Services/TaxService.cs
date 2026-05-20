using Microsoft.EntityFrameworkCore;
using PropertyTax.API.Data;
using PropertyTax.API.DTOs;
using PropertyTax.API.Models;

namespace PropertyTax.API.Services;

public class TaxService
{
    private readonly AppDbContext _dbContext;
    private readonly AuditLogService _auditLogService;

    public TaxService(AppDbContext dbContext, AuditLogService auditLogService)
    {
        _dbContext = dbContext;
        _auditLogService = auditLogService;
    }

    public async Task<TaxDto> CalculateAsync(TaxDto taxDto, string? actorUserId)
    {
        var property = await _dbContext.Properties
            .Include(item => item.Taxpayer)
            .FirstOrDefaultAsync(item => item.Id == taxDto.PropertyId)
            ?? throw new InvalidOperationException("The supplied property could not be found.");

        var marketValue = taxDto.MarketValue ?? property.MarketValue;
        var assessmentLevel = taxDto.AssessmentLevel ?? property.AssessmentLevel;
        var taxRate = taxDto.TaxRate ?? property.TaxRate;

        var normalizedAssessmentLevel = NormalizeRate(assessmentLevel);
        var normalizedTaxRate = NormalizeRate(taxRate);
        var assessedValue = Math.Round(marketValue * normalizedAssessmentLevel, 2, MidpointRounding.AwayFromZero);
        var taxDue = Math.Round(assessedValue * normalizedTaxRate, 2, MidpointRounding.AwayFromZero);

        var assessment = await _dbContext.TaxAssessments
            .FirstOrDefaultAsync(item => item.PropertyId == taxDto.PropertyId && item.TaxYear == taxDto.TaxYear);

        if (assessment is null)
        {
            assessment = new TaxAssessment
            {
                PropertyId = property.Id,
                TaxYear = taxDto.TaxYear,
            };
            _dbContext.TaxAssessments.Add(assessment);
        }

        assessment.MarketValue = marketValue;
        assessment.AssessmentLevel = assessmentLevel;
        assessment.AssessedValue = assessedValue;
        assessment.TaxRate = taxRate;
        assessment.TaxDue = taxDue;
        assessment.CalculatedByUserId = actorUserId;
        assessment.CreatedAtUtc = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();
        await _auditLogService.LogAsync("TaxCalculated", "TaxAssessment", assessment.Id.ToString(), $"Calculated tax for property {property.Pin} ({taxDto.TaxYear})");

        return MapAssessment(assessment, property);
    }

    public async Task<IReadOnlyCollection<TaxDto>> GetAssessmentsAsync()
    {
        var assessments = await _dbContext.TaxAssessments
            .AsNoTracking()
            .Include(assessment => assessment.Property)
            .ThenInclude(property => property.Taxpayer)
            .OrderByDescending(assessment => assessment.TaxYear)
            .ThenByDescending(assessment => assessment.CreatedAtUtc)
            .ToListAsync();

        return assessments.Select(assessment => MapAssessment(assessment, assessment.Property)).ToList();
    }

    private static decimal NormalizeRate(decimal value)
    {
        return value > 1m ? value / 100m : value;
    }

    private static TaxDto MapAssessment(TaxAssessment assessment, Property property)
    {
        return new TaxDto
        {
            Id = assessment.Id,
            PropertyId = property.Id,
            PropertyPin = property.Pin,
            OwnerName = property.Taxpayer.FullName,
            TaxYear = assessment.TaxYear,
            MarketValue = assessment.MarketValue,
            AssessmentLevel = assessment.AssessmentLevel,
            TaxRate = assessment.TaxRate,
            AssessedValue = assessment.AssessedValue,
            TaxDue = assessment.TaxDue,
            CreatedAtUtc = assessment.CreatedAtUtc,
        };
    }
}