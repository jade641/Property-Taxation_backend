using System;

namespace PropertyTax.API.Models;

public class MlTrainingJob
{
    public int Id { get; set; }
    public int ModelId { get; set; }
    public string ParamsJson { get; set; } = "{}";
    public string Status { get; set; } = "Pending";
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string Logs { get; set; } = string.Empty;

    // navigation
    public MlModel? Model { get; set; }
}
