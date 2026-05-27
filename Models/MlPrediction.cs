using System;

namespace PropertyTax.API.Models;

public class MlPrediction
{
    public int Id { get; set; }
    public string SourceType { get; set; } = "Property";
    public string? DatasetName { get; set; }
    public string? DatasetStoredAs { get; set; }
    public int? RowNumber { get; set; }
    public string? ExternalPropertyId { get; set; }
    public string? OwnerSnapshot { get; set; }
    public int? PropertyId { get; set; }
    public int ModelId { get; set; }
    public decimal Probability { get; set; }
    public bool PredictedLabel { get; set; }
    public string ExplanationJson { get; set; } = "{}";
    public string? CreatedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // navigation
    public Property? Property { get; set; }
    public MlModel? Model { get; set; }
    public ApplicationUser? CreatedBy { get; set; }
}
