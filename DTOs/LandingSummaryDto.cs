namespace PropertyTax.API.DTOs;

public sealed class LandingSummaryDto
{
    public int TotalUsers { get; init; }
    public int ActiveUsers { get; init; }
    public int TotalProperties { get; init; }
    public int TotalTaxpayers { get; init; }
    public int TotalAssessments { get; init; }
    public int TotalPayments { get; init; }
    public decimal TotalCollected { get; init; }
    public decimal TotalOutstandingBalance { get; init; }
    public int CompliantCount { get; init; }
    public int LateCount { get; init; }
    public int UnpaidCount { get; init; }
    public decimal ComplianceRate { get; init; }
    public string? LatestCollectionLabel { get; init; }
}