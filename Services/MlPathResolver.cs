using Microsoft.Extensions.Configuration;

namespace PropertyTax.API.Services;

internal static class MlPathResolver
{
    private const string MlFolderName = "PropertyTax_ML";
    private const string SolutionFileName = "PropertyTax.slnx";

    private static readonly string[] MlRootMarkers =
    {
        "train_and_evaluate.py",
        Path.Combine("ml_service", "app.py"),
    };

    private static readonly HashSet<string> DeprioritizedPathSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin",
        "obj",
        "bin-temp",
        "bin-temp.bak",
        "temp_build_output",
        "temp_build_validation",
        "publish",
        "dist",
    };

    public static string? ResolveMlDirectory(IConfiguration configuration, params string?[] searchRoots)
    {
        var configuredRoot = configuration["PropertyTaxMlRoot"]
            ?? configuration["PROPERTYTAX_ML_ROOT"]
            ?? configuration["PROPERTYTAX_ML_DIR"];

        if (TryResolveConfiguredRoot(configuredRoot, out var configuredCandidate))
        {
            return configuredCandidate;
        }

        var solutionRoot = ResolveSolutionRoot(searchRoots);
        if (!string.IsNullOrWhiteSpace(solutionRoot))
        {
            var solutionCandidate = Path.Combine(solutionRoot, MlFolderName);
            if (IsUsableMlDirectory(solutionCandidate))
            {
                return solutionCandidate;
            }
        }

        return EnumerateMlDirectoryCandidates(searchRoots)
            .OrderBy(ScoreCandidate)
            .ThenBy(path => path.Length)
            .FirstOrDefault();
    }

    public static string? ResolveMlArtifactPath(IConfiguration configuration, string fileName, params string?[] searchRoots)
    {
        var mlDirectory = ResolveMlDirectory(configuration, searchRoots);
        if (string.IsNullOrWhiteSpace(mlDirectory))
        {
            return null;
        }

        var artifactPath = Path.Combine(mlDirectory, "models", fileName);
        return File.Exists(artifactPath)
            ? artifactPath
            : null;
    }

    public static string? ResolveSolutionRoot(params string?[] searchRoots)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in NormalizeSearchRoots(searchRoots))
        {
            var current = new DirectoryInfo(root);
            for (var depth = 0; depth < 10 && current is not null; depth += 1)
            {
                if (TryFindSolutionRoot(current.FullName, visited, out var solutionRoot))
                {
                    return solutionRoot;
                }

                foreach (var childDirectory in EnumerateChildDirectories(current.FullName))
                {
                    if (TryFindSolutionRoot(childDirectory, visited, out solutionRoot))
                    {
                        return solutionRoot;
                    }
                }

                current = current.Parent;
            }
        }

        return null;
    }

    private static bool TryResolveConfiguredRoot(string? configuredRoot, out string? resolvedPath)
    {
        resolvedPath = null;
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            return false;
        }

        var normalized = NormalizeFullPath(configuredRoot);
        if (normalized is null || !IsUsableMlDirectory(normalized))
        {
            return false;
        }

        resolvedPath = normalized;
        return true;
    }

    private static IEnumerable<string> EnumerateMlDirectoryCandidates(IEnumerable<string?> searchRoots)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in NormalizeSearchRoots(searchRoots))
        {
            var current = new DirectoryInfo(root);
            for (var depth = 0; depth < 10 && current is not null; depth += 1)
            {
                AddMlDirectoryCandidate(Path.Combine(current.FullName, MlFolderName), candidates);

                foreach (var childDirectory in EnumerateChildDirectories(current.FullName))
                {
                    AddMlDirectoryCandidate(Path.Combine(childDirectory, MlFolderName), candidates);
                }

                current = current.Parent;
            }
        }

        return candidates;
    }

    private static void AddMlDirectoryCandidate(string candidate, ISet<string> candidates)
    {
        var normalized = NormalizeFullPath(candidate);
        if (normalized is null || !IsUsableMlDirectory(normalized))
        {
            return;
        }

        candidates.Add(normalized);
    }

    private static bool TryFindSolutionRoot(string candidateRoot, ISet<string> visited, out string? solutionRoot)
    {
        solutionRoot = null;
        var normalized = NormalizeFullPath(candidateRoot);
        if (normalized is null || !visited.Add(normalized))
        {
            return false;
        }

        if (!File.Exists(Path.Combine(normalized, SolutionFileName)))
        {
            return false;
        }

        solutionRoot = normalized;
        return true;
    }

    private static IEnumerable<string> NormalizeSearchRoots(IEnumerable<string?> searchRoots)
    {
        return searchRoots
            .Select(NormalizeFullPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)!;
    }

    private static IEnumerable<string> EnumerateChildDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory).ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
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

    private static bool IsUsableMlDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        return MlRootMarkers.All(marker => File.Exists(Path.Combine(path, marker)));
    }

    private static int ScoreCandidate(string path)
    {
        var penalty = 0;
        foreach (var segment in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (DeprioritizedPathSegments.Contains(segment))
            {
                penalty += 100;
            }
        }

        return penalty;
    }
}