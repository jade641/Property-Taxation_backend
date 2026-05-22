using System.Globalization;
using System.Net.Http;
using System.Text;
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
        var result = TryBuildModelsFromArtifacts(out var artifactModels);
        _logger.LogInformation("TryBuildModelsFromArtifacts returned: {Result}, model count: {Count}", result, artifactModels.Count);

        if (result)
        {
            _logger.LogInformation("Serving {Count} models from artifacts", artifactModels.Count);
            return Ok(ApiResponse<object>.Ok(artifactModels));
        }

        _logger.LogWarning("TryBuildModelsFromArtifacts returned false, falling back to database");

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

    [HttpDelete("models/cleanup")]
    [Authorize(Roles = SystemRoles.Admin)]
    public async Task<IActionResult> CleanupDuplicateModels()
    {
        // Keep only the most recent model per name, delete others with empty metrics
        var allModels = await _db.MlModels.OrderByDescending(m => m.CreatedAt).ToListAsync();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toDelete = new List<MlModel>();

        foreach (var model in allModels)
        {
            var isEmpty = string.IsNullOrWhiteSpace(model.MetricsJson) || model.MetricsJson == "{}";
            if (isEmpty || seen.Contains(model.Name))
                toDelete.Add(model);
            else
                seen.Add(model.Name);
        }

        _db.MlModels.RemoveRange(toDelete);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = toDelete.Count, remaining = allModels.Count - toDelete.Count });
    }

    [HttpGet("chart/feature-importance")]
    [Authorize(Roles = SystemRoles.Admin + "," + SystemRoles.Auditor + "," + SystemRoles.Accountant + "," + SystemRoles.Staff)]
    public Task<IActionResult> GetFeatureImportanceChart()
        => GetChartFromMlServiceAsync<FeatureImportanceChartResponse>("chart_feature_importance", "chart/feature-importance");

    [HttpGet("chart/risk-distribution")]
    [Authorize(Roles = SystemRoles.Admin + "," + SystemRoles.Auditor + "," + SystemRoles.Accountant + "," + SystemRoles.Staff)]
    public Task<IActionResult> GetRiskDistributionChart([FromQuery] string? dataset)
    {
        var cacheKey = $"chart_risk_distribution:{(string.IsNullOrWhiteSpace(dataset) ? "default" : dataset)}";
        var relativePath = string.IsNullOrWhiteSpace(dataset) ? "chart/risk-distribution" : $"chart/risk-distribution?dataset={Uri.EscapeDataString(dataset)}";
        var ttl = string.IsNullOrWhiteSpace(dataset) ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30);
        return GetChartFromMlServiceAsync<RiskDistributionChartResponse>(cacheKey: cacheKey, relativePath: relativePath, ttl: ttl);
    }

    [HttpGet("chart/probability-histogram")]
    [Authorize(Roles = SystemRoles.Admin + "," + SystemRoles.Auditor + "," + SystemRoles.Accountant + "," + SystemRoles.Staff)]
    public Task<IActionResult> GetProbabilityHistogramChart([FromQuery] string? dataset)
    {
        var cacheKey = $"chart_probability_histogram:{(string.IsNullOrWhiteSpace(dataset) ? "default" : dataset)}";
        var relativePath = string.IsNullOrWhiteSpace(dataset) ? "chart/probability-histogram" : $"chart/probability-histogram?dataset={Uri.EscapeDataString(dataset)}";
        var ttl = string.IsNullOrWhiteSpace(dataset) ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30);
        return GetChartFromMlServiceAsync<ProbabilityHistogramChartResponse>(cacheKey: cacheKey, relativePath: relativePath, ttl: ttl);
    }

    [HttpPost("chart/cache/clear")]
    [Authorize(Roles = SystemRoles.Admin)]
    public IActionResult ClearChartCache()
    {
        _memoryCache.Remove("chart_feature_importance");
        _memoryCache.Remove("chart_risk_distribution:default");
        _memoryCache.Remove("chart_probability_histogram:default");
        return Ok(new { cleared = true });
    }

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

        // Write the file and close the stream before attempting the copy
        await using (var stream = System.IO.File.Create(targetPath))
        {
            await file.CopyToAsync(stream);
        }

        // Copy to shared folder so the Python ML service can find the file
        try
        {
            var sharedUploadsDir = FindSharedUploadsDirectory();
            if (sharedUploadsDir is null)
            {
                // try fallback to solution root -> PropertyTax_ML/datasets/uploads
                var solRoot = FindSolutionRoot();
                if (!string.IsNullOrWhiteSpace(solRoot))
                {
                    var candidate = Path.Combine(solRoot, "PropertyTax_ML", "datasets", "uploads");
                    sharedUploadsDir = candidate;
                }
            }

                if (sharedUploadsDir is not null)
                {
                    _logger.LogInformation("Shared uploads dir resolved to: {Dir}", sharedUploadsDir ?? "null");
                    Directory.CreateDirectory(sharedUploadsDir!);
                    var sharedPath = Path.Combine(sharedUploadsDir!, safeFileName);

                // Ensure file is saved with UTF-8 encoding to avoid reading issues in Python/pandas
                try
                {
                    using var srcStream = System.IO.File.OpenRead(targetPath);
                    using var reader = new System.IO.StreamReader(srcStream, detectEncodingFromByteOrderMarks: true);
                    var content = reader.ReadToEnd();
                    System.IO.File.WriteAllText(sharedPath, content, System.Text.Encoding.UTF8);
                }
                catch (Exception)
                {
                    // Fallback to simple copy if re-encoding fails
                    System.IO.File.Copy(targetPath, sharedPath, overwrite: true);
                }

                _logger.LogInformation("Dataset copied to shared folder: {SharedPath}", sharedPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to copy dataset to shared ML folder (upload itself succeeded).");
        }

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

            // Also remove from the shared folder
            var sharedUploadsDir = FindSharedUploadsDirectory();
            if (sharedUploadsDir is not null)
            {
                var sharedPath = Path.Combine(sharedUploadsDir, storedFileName);
                if (System.IO.File.Exists(sharedPath))
                {
                    System.IO.File.Delete(sharedPath);
                }
            }

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

    private async Task<IActionResult> GetChartFromMlServiceAsync<T>(string cacheKey, string relativePath, TimeSpan? ttl = null) where T : class
    {
        if (_memoryCache.TryGetValue(cacheKey, out T? cached) && cached is not null)
        {
            return Ok(cached);
        }

        var mlServiceUrl = ResolveMlServiceBaseUrl();
        if (string.IsNullOrWhiteSpace(mlServiceUrl))
        {
            return StatusCode(503, new { error = "ML service unavailable" });
        }

        var client = _httpClientFactory.CreateClient(nameof(MlPredictionController));
        client.BaseAddress = new Uri(mlServiceUrl);

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

            _memoryCache.Set(cacheKey, parsed, ttl ?? TimeSpan.FromMinutes(5));
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

    private string? ResolveMlServiceBaseUrl()
    {
        var configuredUrl = _configuration["MlService:BaseUrl"]
            ?? _configuration["MlServiceUrl"]
            ?? Environment.GetEnvironmentVariable("ML_SERVICE_URL");

        if (string.IsNullOrWhiteSpace(configuredUrl))
        {
            return null;
        }

        configuredUrl = configuredUrl.Trim();
        if (!configuredUrl.EndsWith("/", StringComparison.Ordinal))
        {
            configuredUrl += "/";
        }

        return configuredUrl;
    }

    private sealed record RemoteModelMetrics(
        string Name,
        decimal Accuracy,
        decimal Precision,
        decimal Recall,
        decimal F1Score,
        decimal RocAuc);

    private sealed record MlServiceTrainingResult(
        bool Success,
        string? BestModelName,
        JsonElement Metrics,
        string? ArtifactPath,
        List<RemoteModelMetrics>? ModelMetrics);

    private async Task<MlServiceTrainingResult> PostMlServiceTrainingAsync(string modelName, string datasetName, Dictionary<string, object>? parameters)
    {
        var mlServiceUrl = ResolveMlServiceBaseUrl();
        if (string.IsNullOrWhiteSpace(mlServiceUrl))
        {
            throw new InvalidOperationException("ML service unavailable");
        }

        var client = _httpClientFactory.CreateClient(nameof(MlPredictionController));
        client.BaseAddress = new Uri(mlServiceUrl);
        client.Timeout = TimeSpan.FromMinutes(15);

        var body = new
        {
            model = modelName,
            dataset = datasetName,
            parameters,
        };

        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync("train", content);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to reach ML service training endpoint.", ex);
        }

        var payload = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"ML service training request failed with status code {response.StatusCode}. Response: {payload}");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("ML service training endpoint returned invalid JSON.", ex);
        }

        var root = document.RootElement;
        var success = root.TryGetProperty("success", out var successProp) && successProp.GetBoolean();
        if (!success)
        {
            var error = root.TryGetProperty("error", out var errorProp) ? errorProp.GetString() : "Training failed.";
            throw new InvalidOperationException(error);
        }

        var metrics = root.TryGetProperty("metrics", out var metricsProp) ? metricsProp : default;
        var artifactPath = root.TryGetProperty("artifactPath", out var artifactPathProp) ? artifactPathProp.GetString() : null;
        var bestModelName = root.TryGetProperty("bestModelName", out var bestModelNameProp)
            ? bestModelNameProp.GetString()
            : root.TryGetProperty("best_model_name", out var bestModelNameProp2)
                ? bestModelNameProp2.GetString()
                : null;
        var modelMetrics = new List<RemoteModelMetrics>();

        if (root.TryGetProperty("modelMetrics", out var modelMetricsProp) && modelMetricsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in modelMetricsProp.EnumerateArray())
            {
                if (item.TryGetProperty("name", out var nameProp))
                {
                    var name = nameProp.GetString() ?? string.Empty;
                    modelMetrics.Add(new RemoteModelMetrics(
                        Name: name,
                        Accuracy: item.TryGetProperty("accuracy", out var acc) && acc.TryGetDecimal(out var accValue) ? accValue : 0m,
                        Precision: item.TryGetProperty("precision", out var prec) && prec.TryGetDecimal(out var precValue) ? precValue : 0m,
                        Recall: item.TryGetProperty("recall", out var rec) && rec.TryGetDecimal(out var recValue) ? recValue : 0m,
                        F1Score: item.TryGetProperty("f1Score", out var f1) && f1.TryGetDecimal(out var f1Value) ? f1Value : 0m,
                        RocAuc: item.TryGetProperty("rocAuc", out var roc) && roc.TryGetDecimal(out var rocValue) ? rocValue : 0m
                    ));
                }
            }
        }

        return new MlServiceTrainingResult(
            Success: true,
            BestModelName: bestModelName,
            Metrics: metrics,
            ArtifactPath: artifactPath,
            ModelMetrics: modelMetrics.Count > 0 ? modelMetrics : null);
    }

    private bool TryBuildModelsFromArtifacts(out List<object> models)
    {
        models = new List<object>();

        var resultsPath = FindMlArtifactPath("propertytax_model_selection_results.csv");

        if (resultsPath is null)
        {
            _logger.LogWarning("Missing ML artifact path. resultsPath exists: {ResultsExists}", resultsPath != null);
            return false;
        }

        try
        {
            var csvLines = System.IO.File.ReadAllLines(resultsPath)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToArray();

            if (csvLines.Length < 2)
            {
                _logger.LogWarning("Model selection results CSV contains no data rows.");
                return false;
            }

            var headers = ParseCsvLine(csvLines[0]).Select(header => header.Trim().ToLowerInvariant()).ToArray();
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

            _logger.LogInformation("Parsed CSV Headers: {Headers}", string.Join(", ", headers));
            _logger.LogInformation("CSV Column positions - Model: {ModelIdx}, Accuracy: {AccIdx}, Precision: {PrecIdx}, Recall: {RecIdx}, F1: {F1Idx}, RocAuc: {RocAucIdx}, CvMean: {CvMeanIdx}",
                modelIndex, testAccuracyIndex, testPrecisionIndex, testRecallIndex, testF1Index, testRocAucIndex, cvRocAucMeanIndex);

            if (modelIndex < 0 || testAccuracyIndex < 0 || testF1Index < 0 || testRocAucIndex < 0)
            {
                _logger.LogWarning("One or more required CSV column headers are missing.");
                return false;
            }

            var parsedRows = csvLines.Skip(1).Select(ParseCsvLine).Where(values => values.Count > 0).ToList();

            bool HasMeaningfulMetrics(IReadOnlyList<string> values)
            {
                return ReadDecimal(values, testAccuracyIndex) > 0m
                    || ReadDecimal(values, testPrecisionIndex) > 0m
                    || ReadDecimal(values, testRecallIndex) > 0m
                    || ReadDecimal(values, testF1Index) > 0m
                    || ReadDecimal(values, testRocAucIndex) > 0m;
            }

            var rows = parsedRows.Where(HasMeaningfulMetrics).ToList();
            if (rows.Count == 0)
            {
                rows = parsedRows;
            }

            var normalizedRows = new List<(string ModelName, IReadOnlyList<string> Values, decimal Accuracy, decimal Precision, decimal Recall, decimal F1Score, decimal RocAuc, int Order)>();
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

                normalizedRows.Add((
                    modelName,
                    values,
                    ReadDecimal(values, testAccuracyIndex),
                    ReadDecimal(values, testPrecisionIndex),
                    ReadDecimal(values, testRecallIndex),
                    ReadDecimal(values, testF1Index),
                    ReadDecimal(values, testRocAucIndex),
                    i));
            }

            var dedupedRows = normalizedRows
                .GroupBy(row => row.ModelName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(row => row.F1Score)
                    .ThenByDescending(row => row.RocAuc)
                    .ThenByDescending(row => row.Accuracy)
                    .ThenByDescending(row => row.Order)
                    .First())
                .OrderByDescending(row => row.F1Score)
                .ThenByDescending(row => row.RocAuc)
                .ThenByDescending(row => row.Accuracy)
                .ThenBy(row => row.ModelName)
                .ToList();

            if (dedupedRows.Count == 0)
            {
                _logger.LogWarning("No valid model rows could be parsed from the CSV artifacts.");
                return false;
            }

            var bestModelName = dedupedRows[0].ModelName;
            var featureInfoPath = FindMlArtifactPath("propertytax_feature_info.json");
            if (featureInfoPath is not null)
            {
                try
                {
                    var featureInfoBestModel = ReadBestModelName(featureInfoPath);
                    if (!string.IsNullOrWhiteSpace(featureInfoBestModel))
                    {
                        bestModelName = featureInfoBestModel;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unable to read best model name from feature info artifact; falling back to CSV ranking.");
                }
            }

            for (var i = 0; i < dedupedRows.Count; i += 1)
            {
                var row = dedupedRows[i];
                var modelName = row.ModelName;

                var version = "v1.0";
                var displayLabel = $"{modelName} · {version} · {trainedAt:MMM dd, yyyy}";
                var isBestModel = string.Equals(modelName, bestModelName, StringComparison.OrdinalIgnoreCase);

                var cvRocAucMean = ReadDecimal(row.Values, cvRocAucMeanIndex);

                _logger.LogInformation("Row {RowIdx} parsed - Model: {ModelName}, Acc: {Acc}, Prec: {Prec}, Rec: {Rec}, F1: {F1}, RocAuc: {RocAuc}, isBest: {IsBest}",
                    i + 1, modelName, row.Accuracy, row.Precision, row.Recall, row.F1Score, row.RocAuc, isBestModel);

                models.Add(new
                {
                    id = i + 1,
                    name = modelName,
                    version,
                    displayLabel,
                    accuracy = row.Accuracy,
                    precision = row.Precision,
                    recall = row.Recall,
                    f1Score = row.F1Score,
                    rocAuc = row.RocAuc,
                    cvRocAucMean,
                    testAccuracy = row.Accuracy,
                    testPrecision = row.Precision,
                    testRecall = row.Recall,
                    testF1 = row.F1Score,
                    testRocAuc = row.RocAuc,
                    isBestModel,
                    status = isBestModel ? "Active" : "Archived",
                    lastTrainedAt = trainedAt,
                });
            }

            return models.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in TryBuildModelsFromArtifacts while reading CSV artifacts.");
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
        var configuredRoot = _configuration["PropertyTaxMlRoot"]
            ?? _configuration["PROPERTYTAX_ML_ROOT"]
            ?? _configuration["PROPERTYTAX_ML_DIR"];

        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            try
            {
                var configuredCandidate = Path.GetFullPath(configuredRoot);
                if (Directory.Exists(configuredCandidate))
                {
                    return configuredCandidate;
                }
            }
            catch
            {
                // Fall back to the normal search roots if the configured path is invalid.
            }
        }

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

    private string? FindSharedUploadsDirectory()
    {
        var mlDir = FindMlDirectory();
        if (mlDir is not null)
        {
            return Path.Combine(mlDir, "datasets", "uploads");
        }

        var solRoot = FindSolutionRoot();
        if (!string.IsNullOrWhiteSpace(solRoot))
        {
            return Path.Combine(solRoot, "PropertyTax_ML", "datasets", "uploads");
        }

        return null;
    }

    private string? FindSolutionRoot()
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
                var candidate = Path.Combine(current.FullName, "PropertyTax.slnx");
                if (System.IO.File.Exists(candidate))
                {
                    return current.FullName;
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
                return bestModelName.GetString()?.Trim() ?? string.Empty;
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

                job.Logs = $"{job.Logs}{Environment.NewLine}[{DateTime.UtcNow:O}] Running model fit and train-test split evaluation...";
                await db.SaveChangesAsync();

                string output;
                string error = string.Empty;

                var mlServiceUrl = ResolveMlServiceBaseUrl();
                if (!string.IsNullOrWhiteSpace(mlServiceUrl))
                {
                    job.Logs = $"{job.Logs}{Environment.NewLine}[{DateTime.UtcNow:O}] Sending training request to ML service...";
                    await db.SaveChangesAsync();

                    var trainingResult = await PostMlServiceTrainingAsync(modelName, datasetName, null);

                    output = JsonSerializer.Serialize(new
                    {
                        success = true,
                        metrics = trainingResult.Metrics,
                        artifactPath = trainingResult.ArtifactPath,
                        best_model_name = trainingResult.BestModelName,
                        modelMetrics = trainingResult.ModelMetrics,
                    });
                }
                else
                {
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

                    process.Start();

                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();

                    var completed = process.WaitForExit(600000);
                    if (!completed)
                    {
                        process.Kill();
                        throw new TimeoutException("Training process timed out after 10 minutes.");
                    }

                    output = await outputTask;
                    error = await errorTask;

                    if (process.ExitCode != 0)
                    {
                        throw new Exception($"Training process failed with exit code {process.ExitCode}.{Environment.NewLine}Error: {error}");
                    }
                }

                using var doc = JsonDocument.Parse(output);
                var root = doc.RootElement;

                if (root.TryGetProperty("success", out var successProp) && successProp.GetBoolean())
                {
                    JsonElement metricsElement = default;
                    if (root.TryGetProperty("metrics", out var metricsProp))
                    {
                        metricsElement = metricsProp;
                    }

                    var artifactPath = root.TryGetProperty("artifactPath", out var artifactPathProp)
                        ? artifactPathProp.GetString()
                        : null;

                    if (job.Model is not null)
                    {
                        job.Model.MetricsJson = metricsElement.ValueKind != JsonValueKind.Undefined
                            ? metricsElement.GetRawText()
                            : "{}";
                        job.Model.ArtifactPath = artifactPath ?? string.Empty;
                        job.Model.CreatedAt = DateTime.UtcNow;
                    }

                    var bestModelName = root.TryGetProperty("best_model_name", out var bestModelNameProp)
                        ? bestModelNameProp.GetString()
                        : root.TryGetProperty("bestModelName", out var bestModelNameProp2)
                            ? bestModelNameProp2.GetString()
                            : null;

                    // Promote the retrained model to active, deactivate all others.
                    // Prefer the best model name from the ML service if provided.
                    var allModels = await db.MlModels.ToListAsync();
                    foreach (var m in allModels)
                    {
                        if (!string.IsNullOrWhiteSpace(bestModelName))
                        {
                            m.IsActive = string.Equals(m.Name, bestModelName, StringComparison.OrdinalIgnoreCase);
                        }
                        else
                        {
                            m.IsActive = m.Id == job.ModelId;
                        }
                    }

                    job.Status = "Completed";
                    job.FinishedAt = DateTime.UtcNow;
                    job.Logs = $"{job.Logs}{Environment.NewLine}[{DateTime.UtcNow:O}] Training completed successfully! Real computed evaluation metrics stored.";
                    await db.SaveChangesAsync();

                    if (root.TryGetProperty("modelMetrics", out var modelMetricsElement) && modelMetricsElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in modelMetricsElement.EnumerateArray())
                        {
                            if (!item.TryGetProperty("name", out var nameProp))
                            {
                                continue;
                            }

                            var csvModelName = nameProp.GetString()?.Trim();
                            if (string.IsNullOrWhiteSpace(csvModelName))
                            {
                                continue;
                            }

                            var dbModel = await db.MlModels
                                .FirstOrDefaultAsync(m => m.Name.ToLower() == csvModelName.ToLower());

                            if (dbModel is null)
                            {
                                dbModel = new MlModel
                                {
                                    Name = csvModelName,
                                    Version = "v1.0",
                                    MetricsJson = "{}",
                                    ArtifactPath = string.Empty,
                                    IsActive = false,
                                    CreatedAt = DateTime.UtcNow,
                                };
                                db.MlModels.Add(dbModel);
                                await db.SaveChangesAsync();
                            }

                            dbModel.MetricsJson = System.Text.Json.JsonSerializer.Serialize(new
                            {
                                accuracy = item.TryGetProperty("accuracy", out var accProp) && accProp.TryGetDecimal(out var accValue) ? accValue : 0m,
                                precision = item.TryGetProperty("precision", out var precProp) && precProp.TryGetDecimal(out var precValue) ? precValue : 0m,
                                recall = item.TryGetProperty("recall", out var recallProp) && recallProp.TryGetDecimal(out var recallValue) ? recallValue : 0m,
                                f1Score = item.TryGetProperty("f1Score", out var f1Prop) && f1Prop.TryGetDecimal(out var f1Value) ? f1Value : 0m,
                                rocAuc = item.TryGetProperty("rocAuc", out var rocProp) && rocProp.TryGetDecimal(out var rocValue) ? rocValue : 0m,
                            });
                            dbModel.CreatedAt = DateTime.UtcNow;
                        }

                        await db.SaveChangesAsync();
                    }
                    else
                    {
                        // After job.Status = "Completed", update per-model metrics from CSV when remote metrics are unavailable.
                        var csvPath = FindMlArtifactPath("propertytax_model_selection_results.csv");
                        if (csvPath is not null)
                        {
                            var csvLines = System.IO.File.ReadAllLines(csvPath)
                                .Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
                            if (csvLines.Length >= 2)
                            {
                                var headers = ParseCsvLine(csvLines[0])
                                    .Select(h => h.Trim().ToLowerInvariant()).ToArray();
                                var modelIdx = Array.IndexOf(headers, "model");
                                var accIdx = Array.IndexOf(headers, "test_accuracy");
                                var precIdx = Array.IndexOf(headers, "test_precision");
                                var recIdx = Array.IndexOf(headers, "test_recall");
                                var f1Idx = Array.IndexOf(headers, "test_f1");
                                var rocIdx = Array.IndexOf(headers, "test_roc_auc");

                                foreach (var line in csvLines.Skip(1))
                                {
                                    var cols = ParseCsvLine(line);
                                    if (modelIdx < 0 || modelIdx >= cols.Count) continue;
                                    var csvModelName = cols[modelIdx].Trim();
                                    if (string.IsNullOrWhiteSpace(csvModelName)) continue;

                                    var dbModel = await db.MlModels
                                        .FirstOrDefaultAsync(m => m.Name.ToLower() == csvModelName.ToLower());

                                    // Create a new DB entry if the model doesn't exist yet
                                    if (dbModel is null)
                                    {
                                        dbModel = new MlModel
                                        {
                                            Name = csvModelName,
                                            Version = "v1.0",
                                            MetricsJson = "{}",
                                            ArtifactPath = string.Empty,
                                            IsActive = false,
                                            CreatedAt = DateTime.UtcNow,
                                        };
                                        db.MlModels.Add(dbModel);
                                        await db.SaveChangesAsync();
                                    }

                                    dbModel.MetricsJson = System.Text.Json.JsonSerializer.Serialize(new
                                    {
                                        accuracy = ReadDecimal(cols, accIdx),
                                        precision = ReadDecimal(cols, precIdx),
                                        recall = ReadDecimal(cols, recIdx),
                                        f1Score = ReadDecimal(cols, f1Idx),
                                        rocAuc = ReadDecimal(cols, rocIdx),
                                    });
                                    dbModel.CreatedAt = DateTime.UtcNow;
                                }
                                await db.SaveChangesAsync();
                            }
                        }
                    }

                    // After saving per-model metrics, delete duplicate model entries (same name, keep most recent with non-zero metrics)
                    var allModelsAfterUpdate = await db.MlModels.OrderByDescending(m => m.CreatedAt).ToListAsync();
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var toDelete = new List<MlModel>();
                    foreach (var m in allModelsAfterUpdate)
                    {
                        var isEmpty = string.IsNullOrWhiteSpace(m.MetricsJson) || m.MetricsJson.Trim() == "{}";
                        if (isEmpty || seen.Contains(m.Name))
                            toDelete.Add(m);
                        else
                            seen.Add(m.Name);
                    }
                    if (toDelete.Any())
                    {
                        db.MlModels.RemoveRange(toDelete);
                        await db.SaveChangesAsync();
                    }
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
