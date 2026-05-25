using System.Threading;

namespace PropertyTax.API.Services;

public interface IMlServiceCoordinator
{
    Task<bool> EnsureReadyAsync(bool requireLoadedModels = false, CancellationToken cancellationToken = default);
    Task<bool> ReloadAsync(bool forceRestart = false, CancellationToken cancellationToken = default);
}