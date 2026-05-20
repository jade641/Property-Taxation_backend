using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using PropertyTax.API.Data;
using PropertyTax.API.DTOs;
using PropertyTax.API.Models;
using PropertyTax.API.Services;

namespace PropertyTax.API.Controllers;

[ApiController]
[Route("api/ml")]
public class MlPredictionController : ControllerBase
{
    private readonly IMlPredictionService _predictionService;
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _environment;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _memoryCache;
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<MlPredictionController> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public MlPredictionController(
        IMlPredictionService predictionService,
        AppDbContext db,
        IWebHostEnvironment environment,
        IHttpClientFactory httpClientFactory,
        IMemoryCache memoryCache,
        IConfiguration configuration,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<MlPredictionController> logger)
    {
        _predictionService = predictionService;
        _db = db;
        _environment = environment;
        _httpClientFactory = httpClientFactory;
        _memoryCache = memoryCache;
        _configuration = configuration;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    public sealed class CreatePredictionRequest
    {
        public int PropertyId { get; set; }
        public string? ModelName { get; set; }
    }

    public sealed class TrainingRequest
    {
        public string ModelName { get; set; } = string.Empty;
        public string DatasetName { get; set; } = string.Empty;
        public Dictionary<string, object>? Parameters { get; set; }
    }

    private sealed class FeatureImportanceChartResponse
    {
        public List<FeatureImportanceItem> Features { get; set; } = new();
    }

    private sealed class FeatureImportanceItem
    {
        public string Name { get; set; } = string.Empty;
        public decimal Importance { get; set; }
    }

    private sealed class RiskDistributionChartResponse
    {
        public int Low { get; set; }
        public int Medium { get; set; }
        public int High { get; set; }
    }

    private sealed class ProbabilityHistogramChartResponse
    {
        public List<string> Bins { get; set; } = new();
        public List<int> Counts { get; set; } = new();
    }

    private sealed class TrainingStatusResponse
    {
        public string Status { get; set; } = "idle";
        public int Progress { get; set; }
        public string CurrentModel { get; set; } = "N/A";
        public DateTime? LastTrainedAt { get; set; }
        public decimal Accuracy { get; set; }
        public decimal Precision { get; set; }
        public decimal Recall { get; set; }
        public decimal F1Score { get; set; }
        public decimal RocAuc { get; set; }
        public int? JobId { get; set; }
        public string? Message { get; set; }
    }

    [HttpPost("predictions")]
    [Authorize(Roles = SystemRoles.Admin + "," + SystemRoles.Auditor + "," + SystemRoles.Accountant)]
    public async Task<IActionResult> Create(CreatePredictionRequest request)
    {
        var userId = User?.Claims.FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        try
        {
            var prediction = await _predictionService.PredictAsync(request.PropertyId, request.ModelName, userId);
            return CreatedAtAction(nameof(GetById), new { id = prediction.Id }, prediction);
        }
        catch (KeyNotFoundException)
        {
            return NotFound(ApiResponse<object?>.Fail("Property not found."));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<object?>.Fail(ex.Message));
        }
    }

    [HttpGet("predictions")]
    [Authorize(Roles = SystemRoles.Admin + "," + SystemRoles.Auditor + "," + SystemRoles.Accountant + "," + SystemRoles.Staff)]
    public async Task<IActionResult> ListPredictions()
    {
        var predictions = await _db.MlPredictions
            .AsNoTracking()
            .Include(prediction => prediction.Property)
                .ThenInclude(property => property!.Taxpayer)
            .Include(prediction => prediction.Property)
                .ThenInclude(property => property!.Payments)
            .Include(prediction => prediction.Model)
            .OrderByDescending(prediction => prediction.CreatedAt)
            .ToListAsync();

        var items = predictions.Select(prediction => new
        {
            id = prediction.Id,
            propertyId = prediction.PropertyId,
            owner = prediction.Property?.Taxpayer?.FullName
                ?? prediction.Property?.Pin
                ?? prediction.PropertyId.ToString(),
            prediction = prediction.PredictedLabel ? "Late" : "On-time",
            riskLevel = prediction.Probability >= 0.8m ? "High" : prediction.Probability >= 0.5m ? "Medium" : "Low",
            probabilityScore = Math.Round(prediction.Probability * 100m, 1),
            lastPaymentDate = prediction.Property?.Payments
                .OrderByDescending(payment => payment.PaymentDateUtc)
                .Select(payment => payment.PaymentDateUtc)
                .FirstOrDefault(),
            modelName = prediction.Model?.Name ?? "Unknown model",
        }).ToList();

        return Ok(ApiResponse<object>.Ok(items));
    }

    [HttpGet("predictions/{id}")]
    [Authorize]
    public async Task<IActionResult> GetById(int id)
    {
        var prediction = await _db.MlPredictions
            .AsNoTracking()
            .Include(item => item.Property)
                .ThenInclude(property => property!.Taxpayer)
            .Include(item => item.Property)
                .ThenInclude(property => property!.Payments)
            .Include(item => item.Model)
            .FirstOrDefaultAsync(item => item.Id == id);

        if (prediction is null)
        {
            return NotFound(ApiResponse<object?>.Fail("Prediction not found."));
        }

        return Ok(ApiResponse<object>.Ok(BuildPredictionResponse(prediction)));
    }

    [HttpGet("explanations/{id}")]
    [Authorize(Roles = SystemRoles.Admin + "," + SystemRoles.Auditor + "," + SystemRoles.Accountant + "," + SystemRoles.Staff)]
    public async Task<IActionResult> GetExplanation(int id)
    {
        var prediction = await _db.MlPredictions
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id);

        if (prediction is null)
        {
            return NotFound(ApiResponse<object?>.Fail("Prediction not found."));
        }

        return Ok(ApiResponse<object>.Ok(new
        {
            id = prediction.Id,
            predictionId = prediction.Id,
            summary = BuildSummary(prediction.Probability, prediction.PredictedLabel),
            confidenceScore = Math.Round((1m - Math.Abs(0.5m - prediction.Probability) * 2m) * 100m, 1),
            factors = BuildFactors(prediction.Probability),
            rawJson = ParseExplanation(prediction.ExplanationJson),
        }));
    }

    [HttpGet("models")]
    [Authorize(Roles = SystemRoles.Admin + "," + SystemRoles.Auditor + "," + SystemRoles.Accountant + "," + SystemRoles.Staff)]
    public async Task<IActionResult> GetModels()
    {
        if (TryBuildModelsFromArtifacts(out var artifactModels))
        {
            return Ok(ApiResponse<object>.Ok(artifactModels));
        }

        var models = await _db.MlModels
            .AsNoTracking()
            .OrderByDescending(model => model.CreatedAt)
            .ToListAsync();

        var items = models.Select(model => new
        {
            id = model.Id,
            name = model.Name,
            version = model.Version,
            displayLabel = $"{model.Name} · {model.Version}",
            accuracy = ParseMetric(model.MetricsJson, "accuracy"),
            precision = ParseMetric(model.MetricsJson, "precision"),
            recall = ParseMetric(model.MetricsJson, "recall"),
            f1Score = ParseMetric(model.MetricsJson, "f1Score"),
            rocAuc = ParseMetric(model.MetricsJson, "rocAuc"),
            cvRocAucMean = ParseMetric(model.MetricsJson, "cvRocAucMean"),
            testAccuracy = ParseMetric(model.MetricsJson, "testAccuracy"),
            testPrecision = ParseMetric(model.MetricsJson, "testPrecision"),
            testRecall = ParseMetric(model.MetricsJson, "testRecall"),
            testF1 = ParseMetric(model.MetricsJson, "testF1"),
            testRocAuc = ParseMetric(model.MetricsJson, "testRocAuc"),
            isBestModel = model.IsActive,
            status = model.IsActive ? "Active" : "Archived",
            lastTrainedAt = model.CreatedAt,
        }).ToList();

        return Ok(ApiResponse<object>.Ok(items));
    }

    [HttpGet("chart/feature-importance")]
    [Authorize(Roles = SystemRoles.Admin + "," + SystemRoles.Auditor + "," + SystemRoles.Accountant + "," + SystemRoles.Staff)]
    public Task<IActionResult> GetFeatureImportanceChart()
        => GetChartFromMlServiceAsync<FeatureImportanceChartResponse>("chart_feature_importance", "chart/feature-importance");

    [HttpGet("chart/risk-distribution")]
    [Authorize(Roles = SystemRoles.Admin + "," + SystemRoles.Auditor + "," + SystemRoles.Accountant + "," + SystemRoles.Staff)]
    public Task<IActionResult> GetRiskDistributionChart([FromQuery] string? dataset)
        => GetChartFromMlServiceAsync<RiskDistributionChartResponse>(
            cacheKey: $"chart_risk_distribution:{(string.IsNullOrWhiteSpace(dataset) ? "default" : dataset)}",
            relativePath: string.IsNullOrWhiteSpace(dataset) ? "chart/risk-distribution" : $"chart/risk-distribution?dataset={Uri.EscapeDataString(dataset)}"
        );

    [HttpGet("chart/probability-histogram")]
    [Authorize(Roles = SystemRoles.Admin + "," + SystemRoles.Auditor + "," + SystemRoles.Accountant + "," + SystemRoles.Staff)]
    public Task<IActionResult> GetProbabilityHistogramChart([FromQuery] string? dataset)
        => GetChartFromMlServiceAsync<ProbabilityHistogramChartResponse>(
            cacheKey: $"chart_probability_histogram:{(string.IsNullOrWhiteSpace(dataset) ? "default" : dataset)}",
            relativePath: string.IsNullOrWhiteSpace(dataset) ? "chart/probability-histogram" : $"chart/probability-histogram?dataset={Uri.EscapeDataString(dataset)}"
        );

    [HttpGet("training/history")]
    [Authorize(Roles = SystemRoles.Admin + "," + SystemRoles.Auditor + "," + SystemRoles.Accountant + "," + SystemRoles.Staff)]
    public async Task<IActionResult> GetTrainingHistory()
    {
        var jobs = await _db.MlTrainingJobs
            .AsNoTracking()
            .Include(job => job.Model)
            .OrderByDescending(job => job.StartedAt ?? job.FinishedAt)
            .ToListAsync();

        var items = jobs.Select(job => new
        {
            id = job.Id,
            modelName = job.Model?.Name ?? "Unknown model",
            datasetName = ExtractDatasetName(job.ParamsJson),
            status = job.Status,
            startedAt = job.StartedAt,
            finishedAt = job.FinishedAt,
            log = job.Logs,
        }).ToList();

        return Ok(ApiResponse<object>.Ok(items));
    }

    [HttpGet("status")]
    [Authorize(Roles = SystemRoles.Admin + "," + SystemRoles.Auditor + "," + SystemRoles.Accountant + "," + SystemRoles.Staff)]
    public async Task<IActionResult> GetTrainingStatus()
    {
        var activeModel = await _db.MlModels
            .AsNoTracking()
            .Where(model => model.IsActive)
            .OrderByDescending(model => model.CreatedAt)
            .FirstOrDefaultAsync();

        var latestJob = await _db.MlTrainingJobs
            .AsNoTracking()
            .Include(job => job.Model)
            .OrderByDescending(job => job.StartedAt ?? job.FinishedAt)
            .FirstOrDefaultAsync();

        var response = BuildTrainingStatusResponse(latestJob, activeModel);
        return Ok(ApiResponse<object>.Ok(response));
    }

    [HttpPost("training")]
    [Authorize(Roles = SystemRoles.Admin)]
    public async Task<IActionResult> QueueTraining([FromBody] TrainingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ModelName) || string.IsNullOrWhiteSpace(request.DatasetName))
        {
            return BadRequest(ApiResponse<object?>.Fail("ModelName and DatasetName are required."));
        }

        var model = await _db.MlModels.FirstOrDefaultAsync(item => item.Name == request.ModelName);

        if (model is null)
        {
            model = new MlModel
            {
                Name = request.ModelName,
                Version = "v1.0",
                MetricsJson = "{}",
                ArtifactPath = string.Empty,
                IsActive = false,
                CreatedAt = DateTime.UtcNow,
            };

            _db.MlModels.Add(model);
            await _db.SaveChangesAsync();
        }

        var job = new MlTrainingJob
        {
            ModelId = model.Id,
            ParamsJson = JsonSerializer.Serialize(new { request.DatasetName, request.Parameters }),
            Status = "Queued",
            StartedAt = DateTime.UtcNow,
            Logs = $"Training job queued for dataset {request.DatasetName}."
        };

        _db.MlTrainingJobs.Add(job);
        await _db.SaveChangesAsync();

        StartTrainingWorkflow(job.Id, model.Name, request.DatasetName);

        return Ok(ApiResponse<object>.Ok(new
        {
            id = job.Id,
            modelName = model.Name,
            datasetName = request.DatasetName,
            status = job.Status,
            startedAt = job.StartedAt,
            finishedAt = job.FinishedAt,
            log = job.Logs,
        }, "Training job queued successfully."));
    }

    [HttpPost("datasets/upload")]
    [Authorize(Roles = SystemRoles.Admin)]
    public async Task<IActionResult> UploadDataset([FromForm] IFormFile file)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(ApiResponse<object?>.Fail("Uploaded dataset is empty."));
        }

        var uploadsRoot = Path.Combine(AppContext.BaseDirectory, "uploads", "ml-datasets");
        Directory.CreateDirectory(uploadsRoot);

        var safeFileName = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Path.GetFileName(file.FileName)}";
        var targetPath = Path.Combine(uploadsRoot, safeFileName);

        await using var stream = System.IO.File.Create(targetPath);
        await file.CopyToAsync(stream);

        return Ok(ApiResponse<object>.Ok(new
        {
            success = true,
            message = $"Dataset {file.FileName} uploaded successfully.",
            fileName = file.FileName,
            storedAs = safeFileName,
        }));
    }

    [HttpPost("models/{id}/activate")]
    [Authorize(Roles = SystemRoles.Admin)]
    public async Task<IActionResult> ActivateModel(int id)
    {
        var targetModel = await _db.MlModels.FirstOrDefaultAsync(model => model.Id == id);

        if (targetModel is null)
        {
            return NotFound(ApiResponse<object?>.Fail("Model not found."));
        }

        var models = await _db.MlModels.ToListAsync();
        foreach (var model in models)
        {
            model.IsActive = model.Id == id;
        }

        await _db.SaveChangesAsync();

        return Ok(ApiResponse<object>.Ok(new { id = targetModel.Id, status = "Active" }, "Active model updated."));
    }

    [HttpGet("datasets")]
    [Authorize(Roles = SystemRoles.Admin)]
    public IActionResult ListUploadedDatasets()
    {
        var uploadsRoot = Path.Combine(AppContext.BaseDirectory, "uploads", "ml-datasets");
        if (!Directory.Exists(uploadsRoot)) return Ok(ApiResponse<object>.Ok(new object[0]));

        var files = Directory.GetFiles(uploadsRoot)
            .OrderByDescending(f => new FileInfo(f).CreationTimeUtc)
            .Select(f => new {
                fileName = Path.GetFileName(f).Contains('_') ? Path.GetFileName(f).Split('_', 2)[1] : Path.GetFileName(f),
                storedAs = Path.GetFileName(f),
                size = new FileInfo(f).Length,
                createdAt = new FileInfo(f).CreationTimeUtc
            })
            .ToList();

        return Ok(ApiResponse<object>.Ok(files));
    }

    [HttpDelete("datasets/{storedFileName}")]
    [Authorize(Roles = SystemRoles.Admin)]
    public async Task<IActionResult> DeleteUploadedDataset(string storedFileName)
    {
        if (string.IsNullOrWhiteSpace(storedFileName) || storedFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return BadRequest(ApiResponse<object?>.Fail("Invalid file name."));
        }

        var uploadsRoot = Path.Combine(AppContext.BaseDirectory, "uploads", "ml-datasets");
        var targetPath = Path.Combine(uploadsRoot, storedFileName);

        var fullTarget = Path.GetFullPath(targetPath);
        var fullRoot = Path.GetFullPath(uploadsRoot);
        if (!fullTarget.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(ApiResponse<object?>.Fail("Invalid file path."));
        }

        if (!System.IO.File.Exists(fullTarget))
        {
            return NotFound(ApiResponse<object?>.Fail("File not found."));
        }

        try
        {
            System.IO.File.Delete(fullTarget);
            await Task.CompletedTask;
            return Ok(ApiResponse<object>.Ok(new { storedAs = storedFileName }, "Dataset deleted."));
        }
        catch (Exception ex)
        {
            return StatusCode(500, ApiResponse<object?>.Fail($"Failed to delete file: {ex.Message}"));
        }
    }

    private static object BuildPredictionResponse(MlPrediction prediction)
    {
        return new
        {
            id = prediction.Id,
            propertyId = prediction.PropertyId,
            owner = prediction.Property?.Taxpayer?.FullName
                ?? prediction.Property?.Pin
                ?? prediction.PropertyId.ToString(),
            prediction = prediction.PredictedLabel ? "Late" : "On-time",
            riskLevel = prediction.Probability >= 0.8m ? "High" : prediction.Probability >= 0.5m ? "Medium" : "Low",
            probabilityScore = Math.Round(prediction.Probability * 100m, 1),
            lastPaymentDate = prediction.Property?.Payments
                .OrderByDescending(payment => payment.PaymentDateUtc)
                .Select(payment => payment.PaymentDateUtc)
                .FirstOrDefault(),
            modelName = prediction.Model?.Name ?? "Unknown model",
            explanation = ParseExplanation(prediction.ExplanationJson),
        };
    }

    private static object ParseExplanation(string explanationJson)
    {
        if (string.IsNullOrWhiteSpace(explanationJson))
        {
            return new { fallback = false };
        }

        try
        {
            using var document = JsonDocument.Parse(explanationJson);
            return document.RootElement.Clone();
        }
        catch
        {
            return new { raw = explanationJson };
        }
    }

    private static string BuildSummary(decimal probability, bool predictedLabel)
    {
        if (predictedLabel)
        {
            return probability >= 0.8m
                ? "This property is flagged as high risk because the prediction score is elevated."
                : "This property is flagged for review because the prediction score is above the threshold.";
        }

        return "This property is currently predicted to remain on time based on the latest signals.";
    }

    private static object[] BuildFactors(decimal probability)
    {
        var intensity = Math.Round(probability * 100m, 1);
        return new object[] {
            new { name = "Payment history", impact = intensity >= 80m ? 42 : 24 },
            new { name = "Outstanding balance", impact = intensity >= 80m ? 31 : 18 },
            new { name = "Delay frequency", impact = intensity >= 80m ? 21 : 12 },
        };
    }

    private async Task<IActionResult> GetChartFromMlServiceAsync<T>(string cacheKey, string relativePath) where T : class
    {
        if (_memoryCache.TryGetValue(cacheKey, out T? cached) && cached is not null)
        {
            return Ok(cached);
        }

        var mlServiceUrl = _configuration["MlServiceUrl"];
        if (string.IsNullOrWhiteSpace(mlServiceUrl))
        {
            return StatusCode(503, new { error = "ML service unavailable" });
        }

        var client = _httpClientFactory.CreateClient(nameof(MlPredictionController));
        client.BaseAddress = new Uri(mlServiceUrl.TrimEnd('/') + "/");

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(relativePath);
        }
        catch
        {
            return StatusCode(503, new { error = "ML service unavailable" });
        }

        if (!response.IsSuccessStatusCode)
        {
            return StatusCode(503, new { error = "ML service unavailable" });
        }

        try
        {
            var payload = await response.Content.ReadAsStringAsync();
            var parsed = JsonSerializer.Deserialize<T>(payload, JsonOptions);

            if (parsed is null)
            {
                return StatusCode(503, new { error = "ML service unavailable" });
            }

            _memoryCache.Set(cacheKey, parsed, TimeSpan.FromMinutes(5));
            return Ok(parsed);
        }
        catch
        {
            return StatusCode(503, new { error = "ML service unavailable" });
        }
    }

    private static decimal ParseMetric(string metricsJson, string key)
    {
        if (string.IsNullOrWhiteSpace(metricsJson))
        {
            return 0m;
        }

        try
        {
            using var document = JsonDocument.Parse(metricsJson);

            if (document.RootElement.TryGetProperty(key, out var value) && value.TryGetDecimal(out var parsed))
            {
                return parsed;
            }

            if (document.RootElement.TryGetProperty(key, out value) && value.TryGetDouble(out var asDouble))
            {
                return (decimal)asDouble;
            }
        }
        catch
        {
        }

        return 0m;
    }

    private bool TryBuildModelsFromArtifacts(out List<object> models)
    {
        models = new List<object>();

        var resultsPath = FindMlArtifactPath("propertytax_model_selection_results.csv");
        var featureInfoPath = FindMlArtifactPath("propertytax_feature_info.json");

        if (resultsPath is null || featureInfoPath is null)
        {
            return false;
        }

        try
        {
            var csvLines = System.IO.File.ReadAllLines(resultsPath)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToArray();

            if (csvLines.Length < 2)
            {
                return false;
            }

            var headers = ParseCsvLine(csvLines[0]).Select(header => header.Trim().ToLowerInvariant()).ToArray();
            var bestModelName = ReadBestModelName(featureInfoPath);
            var trainedAt = System.IO.File.GetLastWriteTimeUtc(resultsPath);

            int FindColumn(params string[] names)
            {
                for (var i = 0; i < headers.Length; i += 1)
                {
                    if (names.Contains(headers[i]))
                    {
                        return i;
                    }
                }

                return -1;
            }

            var modelIndex = FindColumn("model", "model_name", "name");
            var cvRocAucMeanIndex = FindColumn("cv_roc_auc_mean", "cvrocaucmean", "cvrocauc");
            var testAccuracyIndex = FindColumn("test_accuracy", "testaccuracy");
            var testPrecisionIndex = FindColumn("test_precision", "testprecision");
            var testRecallIndex = FindColumn("test_recall", "testrecall");
            var testF1Index = FindColumn("test_f1", "testf1");
            var testRocAucIndex = FindColumn("test_roc_auc", "testrocauc");

            if (modelIndex < 0 || testAccuracyIndex < 0 || testF1Index < 0 || testRocAucIndex < 0)
            {
                return false;
            }

            var rows = csvLines.Skip(1).Select(ParseCsvLine).Where(values => values.Count > 0).ToList();

            for (var i = 0; i < rows.Count; i += 1)
            {
                var values = rows[i];
                if (modelIndex >= values.Count)
                {
                    continue;
                }

                var modelName = values[modelIndex].Trim();
                if (string.IsNullOrWhiteSpace(modelName))
                {
                    continue;
                }

                var version = "v1.0";
                var displayLabel = $"{modelName} · {version}";
                var isBestModel = string.Equals(modelName, bestModelName, StringComparison.OrdinalIgnoreCase);

                models.Add(new
                {
                    id = i + 1,
                    name = modelName,
                    version,
                    displayLabel,
                    accuracy = ReadDecimal(values, testAccuracyIndex),
                    precision = ReadDecimal(values, testPrecisionIndex),
                    recall = ReadDecimal(values, testRecallIndex),
                    f1Score = ReadDecimal(values, testF1Index),
                    rocAuc = ReadDecimal(values, testRocAucIndex),
                    cvRocAucMean = ReadDecimal(values, cvRocAucMeanIndex),
                    testAccuracy = ReadDecimal(values, testAccuracyIndex),
                    testPrecision = ReadDecimal(values, testPrecisionIndex),
                    testRecall = ReadDecimal(values, testRecallIndex),
                    testF1 = ReadDecimal(values, testF1Index),
                    testRocAuc = ReadDecimal(values, testRocAucIndex),
                    isBestModel,
                    status = isBestModel ? "Active" : "Archived",
                    lastTrainedAt = trainedAt,
                });
            }

            return models.Count > 0;
        }
        catch
        {
            models = new List<object>();
            return false;
        }
    }

    private string? FindMlArtifactPath(string fileName)
    {
        var searchRoots = new[]
        {
            _environment.ContentRootPath,
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
        }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

        foreach (var root in searchRoots)
        {
            var current = new DirectoryInfo(root!);
            for (var depth = 0; depth < 8 && current is not null; depth += 1)
            {
                var candidate = Path.Combine(current.FullName, "PropertyTax_ML", "models", fileName);
                if (System.IO.File.Exists(candidate))
                {
                    return candidate;
                }

                current = current.Parent;
            }
        }

        return null;
    }

    private string? FindMlDirectory()
    {
        var searchRoots = new[]
        {
            _environment.ContentRootPath,
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
        }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

        foreach (var root in searchRoots)
        {
            var current = new DirectoryInfo(root!);
            for (var depth = 0; depth < 8 && current is not null; depth += 1)
            {
                var candidate = Path.Combine(current.FullName, "PropertyTax_ML");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                current = current.Parent;
            }
        }

        return null;
    }

    private static string ReadBestModelName(string featureInfoPath)
    {
        try
        {
            using var document = JsonDocument.Parse(System.IO.File.ReadAllText(featureInfoPath));
            if (document.RootElement.TryGetProperty("best_model_name", out var bestModelName) && bestModelName.ValueKind == JsonValueKind.String)
            {
                return bestModelName.GetString() ?? string.Empty;
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = string.Empty;
        var inQuotes = false;

        for (var i = 0; i < line.Length; i += 1)
        {
            var character = line[i];

            if (character == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current += '"';
                    i += 1;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (character == ',' && !inQuotes)
            {
                values.Add(current.Trim());
                current = string.Empty;
                continue;
            }

            current += character;
        }

        values.Add(current.Trim());
        return values;
    }

    private static decimal ReadDecimal(IReadOnlyList<string> values, int index)
    {
        if (index < 0 || index >= values.Count)
        {
            return 0m;
        }

        return decimal.TryParse(values[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0m;
    }

    private TrainingStatusResponse BuildTrainingStatusResponse(MlTrainingJob? job, MlModel? activeModel)
    {
        var modelName = job?.Model?.Name
            ?? activeModel?.Name
            ?? "N/A";

        var jobStatus = (job?.Status ?? string.Empty).Trim();
        var normalizedStatus = jobStatus.Length == 0 ? "idle" : jobStatus.ToLowerInvariant();
        var lastTrainedAt = job?.FinishedAt ?? activeModel?.CreatedAt ?? job?.StartedAt;

        var progress = normalizedStatus switch
        {
            "queued" => job?.StartedAt is null
                ? 15
                : Math.Min(30, 10 + (int)Math.Floor((DateTime.UtcNow - job.StartedAt.Value).TotalSeconds * 2)),
            "training" => job?.StartedAt is null
                ? 65
                : Math.Min(95, 35 + (int)Math.Floor((DateTime.UtcNow - job.StartedAt.Value).TotalSeconds * 5)),
            "completed" => 100,
            "failed" => 0,
            _ => 0,
        };

        return new TrainingStatusResponse
        {
            Status = normalizedStatus,
            Progress = progress,
            CurrentModel = modelName,
            LastTrainedAt = lastTrainedAt,
            Accuracy = activeModel is null ? 0m : ParseMetric(activeModel.MetricsJson, "accuracy"),
            Precision = activeModel is null ? 0m : ParseMetric(activeModel.MetricsJson, "precision"),
            Recall = activeModel is null ? 0m : ParseMetric(activeModel.MetricsJson, "recall"),
            F1Score = activeModel is null ? 0m : ParseMetric(activeModel.MetricsJson, "f1Score"),
            RocAuc = activeModel is null ? 0m : ParseMetric(activeModel.MetricsJson, "rocAuc"),
            JobId = job?.Id,
            Message = job?.Logs,
        };
    }

    private void StartTrainingWorkflow(int jobId, string modelName, string datasetName)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var job = await db.MlTrainingJobs
                    .Include(item => item.Model)
                    .FirstOrDefaultAsync(item => item.Id == jobId);

                if (job is null)
                {
                    return;
                }

                job.Status = "Training";
                job.Logs = $"{job.Logs}{Environment.NewLine}[{DateTime.UtcNow:O}] Training process spawned for {modelName} on dataset {datasetName}.";
                await db.SaveChangesAsync();

                var mlDir = FindMlDirectory();
                if (string.IsNullOrWhiteSpace(mlDir))
                {
                    throw new DirectoryNotFoundException("PropertyTax_ML folder was not found.");
                }

                var pythonExe = Path.Combine(mlDir, ".venv", "Scripts", "python.exe");
                if (!System.IO.File.Exists(pythonExe))
                {
                    pythonExe = "python";
                }

                var scriptPath = Path.Combine(mlDir, "train_and_evaluate.py");
                if (!System.IO.File.Exists(scriptPath))
                {
                    throw new FileNotFoundException("train_and_evaluate.py script was not found.", scriptPath);
                }

                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = $"\"{scriptPath}\" --model \"{modelName}\" --dataset \"{datasetName}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = mlDir
                };

                using var process = new System.Diagnostics.Process();
                process.StartInfo = startInfo;

                job.Logs = $"{job.Logs}{Environment.NewLine}[{DateTime.UtcNow:O}] Running model fit and train-test split evaluation...";
                await db.SaveChangesAsync();

                process.Start();

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();

                var completed = process.WaitForExit(600000);
                if (!completed)
                {
                    process.Kill();
                    throw new TimeoutException("Training process timed out after 10 minutes.");
                }

                var output = await outputTask;
                var error = await errorTask;

                if (process.ExitCode != 0)
                {
                    throw new Exception($"Training process failed with exit code {process.ExitCode}.{Environment.NewLine}Error: {error}");
                }

                using var doc = JsonDocument.Parse(output);
                var root = doc.RootElement;

                if (root.TryGetProperty("success", out var successProp) && successProp.GetBoolean())
                {
                    var metricsElement = root.GetProperty("metrics");
                    var artifactPath = root.GetProperty("artifactPath").GetString();

                    if (job.Model is not null)
                    {
                        job.Model.MetricsJson = metricsElement.GetRawText();
                        job.Model.ArtifactPath = artifactPath ?? string.Empty;
                        job.Model.CreatedAt = DateTime.UtcNow;
                    }

                    job.Status = "Completed";
                    job.FinishedAt = DateTime.UtcNow;
                    job.Logs = $"{job.Logs}{Environment.NewLine}[{DateTime.UtcNow:O}] Training completed successfully! Real computed evaluation metrics stored.";
                    await db.SaveChangesAsync();
                }
                else
                {
                    var errMsg = root.TryGetProperty("error", out var errProp) ? errProp.GetString() : "Unknown error";
                    throw new Exception($"Training failed: {errMsg}");
                }
            }
            catch (Exception ex)
            {
                try
                {
                    using var scope = _serviceScopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var job = await db.MlTrainingJobs.FirstOrDefaultAsync(item => item.Id == jobId);

                    if (job is not null)
                    {
                        job.Status = "Failed";
                        job.FinishedAt = DateTime.UtcNow;
                        job.Logs = $"{job.Logs}{Environment.NewLine}[{DateTime.UtcNow:O}] Training failed: {ex.Message}";
                        await db.SaveChangesAsync();
                    }
                }
                catch
                {
                    // ignore secondary failure while marking the job failed
                }

                _logger.LogError(ex, "ML training workflow failed for job {JobId}.", jobId);
            }
        });
    }

    private static string ExtractDatasetName(string paramsJson)
    {
        if (string.IsNullOrWhiteSpace(paramsJson))
        {
            return "Unknown dataset";
        }

        try
        {
            using var document = JsonDocument.Parse(paramsJson);

            if (document.RootElement.TryGetProperty("DatasetName", out var datasetName))
            {
                return datasetName.GetString() ?? "Unknown dataset";
            }
        }
        catch
        {
        }

        return "Unknown dataset";
    }
}
