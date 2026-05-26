using Microsoft.Extensions.Configuration;

namespace PropertyTax.API.Services;

internal static class FileStoragePathResolver
{
    private const string DefaultUploadRootSegment = "uploads";
    private const string MlDatasetFolderName = "ml-datasets";
    private const string DataProtectionFolderName = "data-protection-keys";

    public static string ResolveUploadRootPath(string contentRootPath, IConfiguration configuration)
        => ResolveUploadRootPath(contentRootPath, configuration["FileStorage:UploadRoot"]);

    public static string ResolveUploadRootPath(string contentRootPath, string? configuredUploadRoot)
    {
        if (!string.IsNullOrWhiteSpace(configuredUploadRoot))
        {
            return ResolvePath(contentRootPath, configuredUploadRoot);
        }

        var persistentDataRoot = ResolvePersistentDataRoot();
        if (!string.IsNullOrWhiteSpace(persistentDataRoot))
        {
            return Path.Combine(persistentDataRoot, DefaultUploadRootSegment);
        }

        return Path.GetFullPath(Path.Combine(contentRootPath, DefaultUploadRootSegment));
    }

    public static string ResolveUploadRootSegment(IConfiguration configuration)
    {
        var configuredUploadRoot = configuration["FileStorage:UploadRoot"];
        if (string.IsNullOrWhiteSpace(configuredUploadRoot) || Path.IsPathRooted(configuredUploadRoot))
        {
            return DefaultUploadRootSegment;
        }

        return configuredUploadRoot
            .Trim()
            .Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace('\\', '/');
    }

    public static string ResolveStoredRelativePath(IConfiguration configuration, params string[] segments)
    {
        var allSegments = new[] { ResolveUploadRootSegment(configuration) }
            .Concat(segments.Where(segment => !string.IsNullOrWhiteSpace(segment)));

        return Path.Combine(allSegments.ToArray()).Replace('\\', '/');
    }

    public static string ResolveStoredFilePath(string contentRootPath, IConfiguration configuration, string relativePath)
    {
        var uploadRootPath = ResolveUploadRootPath(contentRootPath, configuration);
        var normalizedRelativePath = (relativePath ?? string.Empty)
            .Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var uploadRootSegment = ResolveUploadRootSegment(configuration)
            .Replace('/', Path.DirectorySeparatorChar)
            .Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!string.IsNullOrWhiteSpace(uploadRootSegment))
        {
            var prefix = uploadRootSegment + Path.DirectorySeparatorChar;
            if (normalizedRelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                normalizedRelativePath = normalizedRelativePath[prefix.Length..];
            }
            else if (string.Equals(normalizedRelativePath, uploadRootSegment, StringComparison.OrdinalIgnoreCase))
            {
                normalizedRelativePath = string.Empty;
            }
        }

        return string.IsNullOrWhiteSpace(normalizedRelativePath)
            ? uploadRootPath
            : Path.GetFullPath(Path.Combine(uploadRootPath, normalizedRelativePath));
    }

    public static string ResolveMlDatasetUploadRootPath(string contentRootPath, IConfiguration configuration)
    {
        var configuredDatasetRoot = configuration["FileStorage:MlDatasetUploadRoot"]
            ?? configuration["FileStorage:MlDatasetsUploadRoot"]
            ?? Environment.GetEnvironmentVariable("ML_DATASET_UPLOAD_ROOT");

        if (!string.IsNullOrWhiteSpace(configuredDatasetRoot))
        {
            return ResolvePath(contentRootPath, configuredDatasetRoot);
        }

        return Path.GetFullPath(Path.Combine(ResolveUploadRootPath(contentRootPath, configuration), MlDatasetFolderName));
    }

    public static string ResolveDataProtectionKeyPath(string contentRootPath, string? configuredKeyPath, string uploadRootPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredKeyPath))
        {
            return ResolvePath(contentRootPath, configuredKeyPath);
        }

        var persistentDataRoot = ResolvePersistentDataRoot();
        if (!string.IsNullOrWhiteSpace(persistentDataRoot))
        {
            return Path.Combine(persistentDataRoot, DataProtectionFolderName);
        }

        return Path.GetFullPath(Path.Combine(uploadRootPath, ".keys"));
    }

    private static string? ResolvePersistentDataRoot()
    {
        var configuredPersistentRoot = Environment.GetEnvironmentVariable("PERSISTENT_DATA_ROOT");
        var normalizedConfiguredRoot = NormalizeFullPath(configuredPersistentRoot);
        if (!string.IsNullOrWhiteSpace(normalizedConfiguredRoot))
        {
            return normalizedConfiguredRoot;
        }

        const string defaultPersistentRoot = "/var/data";
        return Directory.Exists(defaultPersistentRoot) ? defaultPersistentRoot : null;
    }

    private static string ResolvePath(string contentRootPath, string configuredPath)
    {
        return Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(Path.Combine(contentRootPath, configuredPath));
    }

    private static string? NormalizeFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }
}