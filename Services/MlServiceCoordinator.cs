using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PropertyTax.API.Services;

public sealed class MlServiceCoordinator : IHostedService, IMlServiceCoordinator, IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MlServiceCoordinator> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Process? _localProcess;
    private bool _disposed;

    public MlServiceCoordinator(
        IConfiguration configuration,
        IHostEnvironment environment,
        IHttpClientFactory httpClientFactory,
        ILogger<MlServiceCoordinator> logger)
    {
        _configuration = configuration;
        _environment = environment;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
        => EnsureReadyAsync(false, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken)
        => StopLocalProcessAsync(cancellationToken);

    public async Task<bool> EnsureReadyAsync(bool requireLoadedModels = false, CancellationToken cancellationToken = default)
    {
        var baseUrl = ResolveMlServiceBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return false;
        }

        var initialState = await GetServiceStateAsync(baseUrl, cancellationToken);
        if (IsStateReady(initialState, requireLoadedModels))
        {
            return true;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var serviceUri))
        {
            return false;
        }

        if (!IsLoopbackUri(serviceUri))
        {
            if (initialState == MlServiceState.HealthyNoModels)
            {
                try
                {
                    await PostReloadAsync(baseUrl, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to reload remote ML service at {BaseUrl}. Continuing to wait for readiness.", baseUrl);
                }
            }

            var remoteWaitTimeout = requireLoadedModels
                ? TimeSpan.FromSeconds(60)
                : TimeSpan.FromSeconds(30);

            return await WaitForReadyStateAsync(baseUrl, requireLoadedModels, remoteWaitTimeout, cancellationToken);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await GetServiceStateAsync(baseUrl, cancellationToken);
            if (IsStateReady(state, requireLoadedModels))
            {
                return true;
            }

            if (state == MlServiceState.HealthyNoModels)
            {
                await PostReloadAsync(baseUrl, cancellationToken);
                return await WaitForReadyStateAsync(baseUrl, requireLoadedModels, TimeSpan.FromSeconds(20), cancellationToken);
            }

            if (_localProcess is { HasExited: false })
            {
                return await WaitForReadyStateAsync(baseUrl, requireLoadedModels, TimeSpan.FromSeconds(30), cancellationToken);
            }

            return await StartLocalProcessAsync(baseUrl, serviceUri, requireLoadedModels, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure the ML service is ready.");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ReloadAsync(bool forceRestart = false, CancellationToken cancellationToken = default)
    {
        var baseUrl = ResolveMlServiceBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var serviceUri))
        {
            return false;
        }

        var isLoopback = IsLoopbackUri(serviceUri);
        if (forceRestart)
        {
            return isLoopback && await ForceRestartLoopbackServiceAsync(baseUrl, serviceUri, requireLoadedModels: true, cancellationToken);
        }

        var serviceReady = await EnsureReadyAsync(false, cancellationToken);
        if (!serviceReady)
        {
            return isLoopback && await ForceRestartLoopbackServiceAsync(baseUrl, serviceUri, requireLoadedModels: true, cancellationToken);
        }

        var restartRequired = false;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await PostReloadAsync(baseUrl, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reload the ML service at {BaseUrl}.", baseUrl);
            restartRequired = isLoopback;
        }
        finally
        {
            _gate.Release();
        }

        if (restartRequired)
        {
            _logger.LogWarning("Loopback ML service reload failed at {BaseUrl}. Attempting forced local restart.", baseUrl);
            return await ForceRestartLoopbackServiceAsync(baseUrl, serviceUri, requireLoadedModels: true, cancellationToken);
        }

        var ready = await WaitForReadyStateAsync(baseUrl, true, TimeSpan.FromSeconds(30), cancellationToken);
        if (!ready && isLoopback)
        {
            _logger.LogWarning("Loopback ML service did not become ready after reload at {BaseUrl}. Attempting forced local restart.", baseUrl);
            return await ForceRestartLoopbackServiceAsync(baseUrl, serviceUri, requireLoadedModels: true, cancellationToken);
        }

        return ready;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
        _localProcess?.Dispose();
    }

    private async Task StopLocalProcessAsync(CancellationToken cancellationToken)
    {
        Process? process;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            process = _localProcess;
            _localProcess = null;
        }
        finally
        {
            _gate.Release();
        }

        if (process is null)
        {
            return;
        }

        await StopTrackedProcessAsync(process, cancellationToken);
    }

    private async Task StopTrackedProcessAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop the local ML service process cleanly.");
        }
        finally
        {
            process.Dispose();
        }
    }

    private async Task<bool> ForceRestartLoopbackServiceAsync(string baseUrl, Uri serviceUri, bool requireLoadedModels, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var trackedProcess = _localProcess;
            _localProcess = null;

            if (trackedProcess is not null)
            {
                await StopTrackedProcessAsync(trackedProcess, cancellationToken);
            }

            await TryTerminateExternalListenerAsync(serviceUri.Port, cancellationToken);

            var listenerReleased = await WaitForStateAsync(baseUrl, MlServiceState.Unavailable, TimeSpan.FromSeconds(10), cancellationToken);
            if (!listenerReleased)
            {
                _logger.LogWarning("Unable to clear the existing ML service listener on port {Port}; forced restart was aborted.", serviceUri.Port);
                return false;
            }

            return await StartLocalProcessAsync(baseUrl, serviceUri, requireLoadedModels, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to force-restart the loopback ML service at {BaseUrl}.", baseUrl);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> StartLocalProcessAsync(string baseUrl, Uri serviceUri, bool requireLoadedModels, CancellationToken cancellationToken)
    {
        var mlDirectory = FindMlDirectory();
        if (string.IsNullOrWhiteSpace(mlDirectory))
        {
            _logger.LogError("PropertyTax_ML directory could not be found. Local ML service cannot be started.");
            return false;
        }

        var pythonExecutable = ResolvePythonExecutable(mlDirectory);
        var host = string.Equals(serviceUri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            ? "127.0.0.1"
            : serviceUri.Host;

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = pythonExecutable,
                Arguments = $"-m uvicorn ml_service.app:app --host {host} --port {serviceUri.Port}",
                WorkingDirectory = mlDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };

        process.StartInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                _logger.LogInformation("[ml_service] {Message}", eventArgs.Data);
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                _logger.LogWarning("[ml_service] {Message}", eventArgs.Data);
            }
        };
        process.Exited += (_, _) =>
        {
            _logger.LogWarning("Local ML service process exited with code {ExitCode}.", process.ExitCode);
            if (ReferenceEquals(_localProcess, process))
            {
                _localProcess = null;
            }
        };

        _logger.LogInformation("Starting local ML service using {PythonExecutable} in {WorkingDirectory}.", pythonExecutable, mlDirectory);
        if (!process.Start())
        {
            _logger.LogError("Failed to start the local ML service process.");
            return false;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _localProcess = process;

        return await WaitForReadyStateAsync(baseUrl, requireLoadedModels, TimeSpan.FromSeconds(45), cancellationToken);
    }

    private async Task<bool> WaitForStateAsync(string baseUrl, MlServiceState expectedState, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            var state = await GetServiceStateAsync(baseUrl, cancellationToken);
            if (state == expectedState)
            {
                return true;
            }

            await Task.Delay(500, cancellationToken);
        }

        return false;
    }

    private async Task TryTerminateExternalListenerAsync(int port, CancellationToken cancellationToken)
    {
        foreach (var processId in GetListeningProcessIds(port))
        {
            if (processId == Environment.ProcessId)
            {
                continue;
            }

            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    continue;
                }

                _logger.LogWarning("Terminating unmanaged ML service listener process {ProcessId} on port {Port}.", processId, port);
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to terminate listener process {ProcessId} on port {Port}.", processId, port);
            }
        }
    }

    private IReadOnlyCollection<int> GetListeningProcessIds(int port)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<int>();
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "netstat",
                    Arguments = "-ano -p tcp",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            var processIds = new HashSet<int>();
            foreach (var line in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = Regex.Split(line.Trim(), "\\s+");
                if (parts.Length < 5)
                {
                    continue;
                }

                if (!string.Equals(parts[0], "TCP", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(parts[3], "LISTENING", StringComparison.OrdinalIgnoreCase)
                    || !parts[1].EndsWith($":{port}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (int.TryParse(parts[4], out var processId))
                {
                    processIds.Add(processId);
                }
            }

            return processIds;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inspect listening process IDs for port {Port}.", port);
            return Array.Empty<int>();
        }
    }

    private async Task<bool> WaitForReadyStateAsync(string baseUrl, bool requireLoadedModels, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            var state = await GetServiceStateAsync(baseUrl, cancellationToken);
            if (IsStateReady(state, requireLoadedModels))
            {
                return true;
            }

            if (_localProcess is { HasExited: true })
            {
                return false;
            }

            await Task.Delay(1000, cancellationToken);
        }

        return false;
    }

    private async Task<MlServiceState> GetServiceStateAsync(string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(nameof(MlServiceCoordinator));
            client.BaseAddress = new Uri(AppendTrailingSlash(baseUrl));
            client.Timeout = TimeSpan.FromSeconds(5);

            using var response = await client.GetAsync("health", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return MlServiceState.Unavailable;
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return MlServiceState.HealthyNoModels;
            }

            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("modelsLoaded", out var modelsLoaded)
                && modelsLoaded.ValueKind == JsonValueKind.Array)
            {
                return modelsLoaded.GetArrayLength() > 0
                    ? MlServiceState.HealthyWithModels
                    : MlServiceState.HealthyNoModels;
            }

            return MlServiceState.HealthyNoModels;
        }
        catch
        {
            return MlServiceState.Unavailable;
        }
    }

    private async Task PostReloadAsync(string baseUrl, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(MlServiceCoordinator));
        client.BaseAddress = new Uri(AppendTrailingSlash(baseUrl));
        client.Timeout = TimeSpan.FromSeconds(30);

        using var response = await client.PostAsync("reload", content: null, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"ML service reload failed with status {(int)response.StatusCode}. Response: {payload}");
    }

    private static bool IsStateReady(MlServiceState state, bool requireLoadedModels)
    {
        return state switch
        {
            MlServiceState.HealthyWithModels => true,
            MlServiceState.HealthyNoModels when !requireLoadedModels => true,
            _ => false,
        };
    }

    private string? ResolveMlServiceBaseUrl()
    {
        var configuredUrl = _configuration["MlService:BaseUrl"]
            ?? _configuration["MlServiceUrl"]
            ?? Environment.GetEnvironmentVariable("ML_SERVICE_URL")
            ?? (_environment.IsDevelopment() ? "http://localhost:8000" : null);

        if (string.IsNullOrWhiteSpace(configuredUrl))
        {
            return null;
        }

        return AppendTrailingSlash(configuredUrl.Trim());
    }

    private static string AppendTrailingSlash(string value)
    {
        return value.EndsWith("/", StringComparison.Ordinal) ? value : $"{value}/";
    }

    private static bool IsLoopbackUri(Uri uri)
    {
        return uri.IsLoopback
            || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase);
    }

    private string ResolvePythonExecutable(string mlDirectory)
    {
        var directCandidates = new[]
        {
            Path.Combine(mlDirectory, ".venv", "Scripts", "python.exe"),
            Path.Combine(mlDirectory, ".venv", "bin", "python"),
        };

        foreach (var candidate in directCandidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var searchRoots = new[]
        {
            mlDirectory,
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
                var workspaceCandidate = Path.Combine(current.FullName, ".venv", "Scripts", "python.exe");
                if (File.Exists(workspaceCandidate))
                {
                    return workspaceCandidate;
                }

                current = current.Parent;
            }
        }

        return "python";
    }

    private string? FindMlDirectory()
    {
        return MlPathResolver.ResolveMlDirectory(
            _configuration,
            _environment.ContentRootPath,
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory());
    }

    private enum MlServiceState
    {
        Unavailable,
        HealthyNoModels,
        HealthyWithModels,
    }
}
