using PropertyTax.API.Models;

namespace PropertyTax.API.Services;

public interface IMlPredictionService
{
    Task<MlPrediction> PredictAsync(int propertyId, string? modelName, string? requestedById);
}
