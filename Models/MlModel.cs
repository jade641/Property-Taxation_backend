using System;
using System.Collections.Generic;

namespace PropertyTax.API.Models;

public class MlModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string MetricsJson { get; set; } = "{}";
    public string ArtifactPath { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string? CreatedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // navigation
    public ApplicationUser? CreatedBy { get; set; }
    public ICollection<MlPrediction> Predictions { get; set; } = new List<MlPrediction>();
    public ICollection<MlTrainingJob> TrainingJobs { get; set; } = new List<MlTrainingJob>();
}
