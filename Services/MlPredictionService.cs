using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PropertyTax.API.Data;
using PropertyTax.API.Models;

namespace PropertyTax.API.Services;

public class MlPredictionService : IMlPredictionService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<MlPredictionService> _logger;

    public MlPredictionService(AppDbContext db, IHttpClientFactory httpFactory, IConfiguration config, ILogger<MlPredictionService> logger)
    {
        _db = db;
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    private static string NormalizeModelName(string value)
    {
        return new string(value.ToLowerInvariant()
            .Where(ch => char.IsLetterOrDigit(ch))
            .ToArray());
    }

    public async Task<MlPrediction> PredictAsync(int propertyId, string? modelName, string? requestedById)
    {
        var property = await _db.Properties
            .Include(p => p.Payments)
            .Include(p => p.TaxAssessments)
            .Include(p => p.Taxpayer)
            .FirstOrDefaultAsync(p => p.Id == propertyId);

        if (property is null)
            throw new KeyNotFoundException("Property not found.");

        MlModel? model = null;

        if (!string.IsNullOrWhiteSpace(modelName))
        {
            var requestedModelName = NormalizeModelName(modelName);
            var modelCandidates = await _db.MlModels.AsNoTracking().ToListAsync();
            model = modelCandidates.FirstOrDefault(m => NormalizeModelName(m.Name) == requestedModelName);
        }

        if (model is null)
        {
            model = await _db.MlModels.Where(m => m.IsActive).OrderByDescending(m => m.CreatedAt).FirstOrDefaultAsync();
        }

        if (model is null)
        {
            throw new InvalidOperationException("No ML model available. Create or activate a model before requesting predictions.");
        }

        // Build feature payload using feature info ordering - missing values are normalized here and revalidated by the ML service.
        var featureInfoPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "PropertyTax_ML", "models", "propertytax_feature_info.json");
        Dictionary<string, object?> featurePayload = new Dictionary<string, object?>();

        try
        {
            var featureInfoJson = File.Exists(featureInfoPath)
                ? await File.ReadAllTextAsync(featureInfoPath)
                : null;

            List<string> allFeatures;

            if (!string.IsNullOrEmpty(featureInfoJson))
            {
                using var doc = JsonDocument.Parse(featureInfoJson);
                allFeatures = doc.RootElement.GetProperty("all_features").EnumerateArray().Select(e => e.GetString() ?? string.Empty).Where(s => !string.IsNullOrEmpty(s)).ToList();
            }
            else
            {
                // fallback: attempt common mapping - best effort
                allFeatures = new List<string>
                {
                    "taxpayer_type","mailing_city","mailing_province","province","city_municipality","barangay","property_type","class_code","zoning_classification","land_use","unit_no",
                    "lot_area_sqm","market_value","assessment_level","assessed_value","tax_rate","tax_amount","assessment_year","years_as_owner","prior_assessments","prior_late_payments","prior_unpaid_payments","avg_previous_delay_days","outstanding_balance","payment_compliance_score","due_month","due_quarter","log_market_value","log_assessed_value","log_outstanding_balance"
                };
            }

            // compute derived features from existing entities
            var latestAssessment = property.TaxAssessments.OrderByDescending(t => t.TaxYear).FirstOrDefault();
            var payments = property.Payments.OrderByDescending(p => p.PaymentDateUtc).ToList();
            var currentYear = DateTime.UtcNow.Year;

            var propertyProvince = property.BarangayLocation?.CityMunicipality?.Province?.Name
                ?? property.Municipality;

            var propertyCityMunicipality = property.BarangayLocation?.CityMunicipality?.Name
                ?? property.Municipality;

            var mailingAddress = property.Taxpayer?.Address ?? string.Empty;
            string mailingCity = "N/A";
            string mailingProvince = "N/A";
            if (!string.IsNullOrWhiteSpace(mailingAddress))
            {
                var parts = mailingAddress.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2) mailingCity = parts[parts.Length - 2];
                else if (parts.Length == 1) mailingCity = parts[0];

                if (parts.Length >= 1) mailingProvince = parts[parts.Length - 1];
            }

            if (mailingCity == "N/A")
                mailingCity = propertyCityMunicipality ?? "N/A";
            if (mailingProvince == "N/A")
                mailingProvince = propertyProvince ?? "N/A";

            var taxpayerName = property.Taxpayer?.FullName ?? string.Empty;
            var companyMarkers = new[] { "inc", "corp", "co", "ltd", "enterprises", "holdings", "partners" };
            var taxpayerType = companyMarkers.Any(marker => taxpayerName.Contains(marker, StringComparison.OrdinalIgnoreCase)) ? "Company" : "Individual";

            var classCode = !string.IsNullOrWhiteSpace(property.PropertyType) ? property.PropertyType.Trim() : "N/A";
            var landUse = !string.IsNullOrWhiteSpace(property.ZoningClassification) ? property.ZoningClassification.Trim() : "N/A";
            var unitNo = !string.IsNullOrWhiteSpace(property.LotNumber) ? property.LotNumber.Trim() : "N/A";
            var yearsAsOwner = Math.Max(0, currentYear - property.DateRegisteredUtc.Year);

            int priorLate = payments.Count(p => p.Penalty > 0 || p.PaymentDateUtc > p.DueDateUtc || p.Status?.ToLower() != "paid");
            int priorUnpaid = payments.Count(p => p.AmountPaid < p.AmountDue);
            double avgDelayDays = payments.Any() ? payments.Average(p => (p.PaymentDateUtc - p.DueDateUtc).TotalDays > 0 ? (p.PaymentDateUtc - p.DueDateUtc).TotalDays : 0) : 0.0;
            decimal outstanding = payments.Where(p => p.AmountPaid < p.AmountDue).Sum(p => p.AmountDue - p.AmountPaid);
            double paymentCompliance = payments.Any() ? (double)payments.Count(p => p.AmountPaid >= p.AmountDue && p.Penalty == 0) / payments.Count : 1.0;

            foreach (var f in allFeatures)
            {
                switch (f)
                {
                    case "taxpayer_type": featurePayload[f] = taxpayerType; break;
                    case "mailing_city": featurePayload[f] = mailingCity; break;
                    case "mailing_province": featurePayload[f] = mailingProvince; break;
                    case "province": featurePayload[f] = propertyProvince ?? "N/A"; break;
                    case "city_municipality": featurePayload[f] = propertyCityMunicipality ?? "N/A"; break;
                    case "barangay": featurePayload[f] = !string.IsNullOrWhiteSpace(property.Barangay) ? property.Barangay.Trim() : "N/A"; break;
                    case "property_type": featurePayload[f] = !string.IsNullOrWhiteSpace(property.PropertyType) ? property.PropertyType.Trim() : "N/A"; break;
                    case "class_code": featurePayload[f] = classCode; break;
                    case "zoning_classification": featurePayload[f] = !string.IsNullOrWhiteSpace(property.ZoningClassification) ? property.ZoningClassification.Trim() : "N/A"; break;
                    case "land_use": featurePayload[f] = landUse; break;
                    case "unit_no": featurePayload[f] = unitNo; break;
                    case "lot_area_sqm": featurePayload[f] = property.AreaSquareMeters; break;
                    case "market_value": featurePayload[f] = property.MarketValue; break;
                    case "assessment_level": featurePayload[f] = latestAssessment?.AssessmentLevel ?? property.AssessmentLevel; break;
                    case "assessed_value": featurePayload[f] = latestAssessment?.AssessedValue ?? 0m; break;
                    case "tax_rate": featurePayload[f] = latestAssessment?.TaxRate ?? property.TaxRate; break;
                    case "tax_amount": featurePayload[f] = latestAssessment?.TaxDue ?? 0m; break;
                    case "assessment_year": featurePayload[f] = latestAssessment?.TaxYear ?? 0; break;
                    case "years_as_owner": featurePayload[f] = yearsAsOwner; break;
                    case "prior_assessments": featurePayload[f] = property.TaxAssessments.Count; break;
                    case "prior_late_payments": featurePayload[f] = priorLate; break;
                    case "prior_unpaid_payments": featurePayload[f] = priorUnpaid; break;
                    case "avg_previous_delay_days": featurePayload[f] = avgDelayDays; break;
                    case "outstanding_balance": featurePayload[f] = outstanding; break;
                    case "payment_compliance_score": featurePayload[f] = paymentCompliance; break;
                    case "due_month": featurePayload[f] = payments.FirstOrDefault()?.DueDateUtc.Month ?? 0; break;
                    case "due_quarter": featurePayload[f] = payments.FirstOrDefault() is Payment p ? ((p.DueDateUtc.Month - 1) / 3 + 1) : 0; break;
                    case "log_market_value":
                    case "log_assessed_value":
                    case "log_outstanding_balance":
                        // Let Python compute log features
                        featurePayload[f] = null;
                        break;
                    default:
                        featurePayload[f] = "N/A";
                        _logger.LogWarning("Missing feature {Feature} for property {PropertyId}; using fallback N/A", f, propertyId);
                        break;
                }
            }

            foreach (var requiredFeature in allFeatures)
            {
                if (!featurePayload.ContainsKey(requiredFeature) || featurePayload[requiredFeature] is null)
                {
                    if (requiredFeature.StartsWith("log_", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    _logger.LogError("Required feature {Feature} missing for property {PropertyId}; using safe fallback.", requiredFeature, propertyId);
                    featurePayload[requiredFeature] = requiredFeature.StartsWith("log_", StringComparison.OrdinalIgnoreCase)
                        ? 0d
                        : (requiredFeature is "lot_area_sqm" or "market_value" or "assessment_level" or "assessed_value" or "tax_rate" or "tax_amount" or "assessment_year" or "years_as_owner" or "prior_assessments" or "prior_late_payments" or "prior_unpaid_payments" or "avg_previous_delay_days" or "outstanding_balance" or "payment_compliance_score" or "due_month" or "due_quarter")
                            ? 0
                            : "N/A";
                }

                if (featurePayload[requiredFeature] is string stringValue && string.IsNullOrWhiteSpace(stringValue))
                {
                    if (requiredFeature.StartsWith("log_", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    _logger.LogWarning("Blank string encountered for feature {Feature} on property {PropertyId}; replacing with safe default.", requiredFeature, propertyId);
                    featurePayload[requiredFeature] = requiredFeature.StartsWith("log_", StringComparison.OrdinalIgnoreCase)
                        ? 0d
                        : (requiredFeature is "lot_area_sqm" or "market_value" or "assessment_level" or "assessed_value" or "tax_rate" or "tax_amount" or "assessment_year" or "years_as_owner" or "prior_assessments" or "prior_late_payments" or "prior_unpaid_payments" or "avg_previous_delay_days" or "outstanding_balance" or "payment_compliance_score" or "due_month" or "due_quarter")
                            ? 0
                            : "N/A";
                }
            }

            _logger.LogDebug("Final feature payload for property {PropertyId}: {Payload}", propertyId, JsonSerializer.Serialize(featurePayload));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed building feature payload for property {PropertyId}", propertyId);
            throw;
        }

        // Call ML microservice
        var mlBase = _config.GetValue<string>("MlService:BaseUrl") ?? "http://localhost:8000";
        var timeoutSecs = _config.GetValue<int?>("MlService:TimeoutSeconds") ?? 10;
        var retries = _config.GetValue<int?>("MlService:RetryCount") ?? 2;

        var client = _httpFactory.CreateClient("mlclient");
        client.BaseAddress = new Uri(mlBase);

        MlPrediction resultPrediction = new MlPrediction
        {
            PropertyId = propertyId,
            ModelId = model.Id,
            CreatedById = requestedById,
            CreatedAt = DateTime.UtcNow,
            ExplanationJson = "{}",
            Probability = 0m,
            PredictedLabel = false
        };

        var payload = new { model = model.Name, features = featurePayload };

        for (int attempt = 0; attempt <= retries; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSecs));
                var response = await client.PostAsJsonAsync("/predict", payload, cts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    var text = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("ML service returned {Status}: {Text}", response.StatusCode, text);
                    continue;
                }

                var content = await response.Content.ReadAsStringAsync(cts.Token);
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                var probability = root.GetProperty("probability").GetDouble();
                var predictedLabel = root.GetProperty("predictedLabel").GetInt32();
                var riskLevel = root.GetProperty("riskLevel").GetString() ?? "Unknown";

                resultPrediction.Probability = (decimal)probability;
                resultPrediction.PredictedLabel = predictedLabel != 0;
                resultPrediction.ExplanationJson = content;

                _db.MlPredictions.Add(resultPrediction);

                if (string.Equals(riskLevel, "High", StringComparison.OrdinalIgnoreCase))
                {
                    // persist an alert record
                    var alert = new MlAlert
                    {
                        PropertyId = propertyId,
                        Title = "High risk: possible late payment",
                        Description = $"ML predicted high risk (model={model.Name}, probability={(decimal)probability:0.00}).",
                        Severity = "High",
                        Status = "Open",
                        CreatedAt = DateTime.UtcNow
                    };

                    _db.MlAlerts.Add(alert);
                }

                await _db.SaveChangesAsync();

                return resultPrediction;
            }
            catch (OperationCanceledException) when (attempt < retries)
            {
                _logger.LogWarning("ML service request timed out, attempt {Attempt}", attempt + 1);
            }
            catch (Exception ex) when (attempt < retries)
            {
                _logger.LogWarning(ex, "ML service request failed, attempt {Attempt}", attempt + 1);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ML service request failed permanently");
                // fallback: persist a failed prediction entry with explanation
                resultPrediction.ExplanationJson = JsonSerializer.Serialize(new { error = ex.Message });
                resultPrediction.Probability = 0m;
                resultPrediction.PredictedLabel = false;
                _db.MlPredictions.Add(resultPrediction);
                await _db.SaveChangesAsync();
                return resultPrediction;
            }
        }

        // if we exhausted retries without success, log and persist a fallback
        _logger.LogError("ML service unreachable after {Retries} attempts", retries + 1);
        resultPrediction.ExplanationJson = JsonSerializer.Serialize(new { error = "ML service unreachable" });
        _db.MlPredictions.Add(resultPrediction);
        await _db.SaveChangesAsync();
        return resultPrediction;
    }
}
