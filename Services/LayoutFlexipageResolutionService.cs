using System.Text.RegularExpressions;
using eZBERP_AI_IDE.Models;

namespace eZBERP_AI_IDE.Services;

public sealed class LayoutFlexipageResolutionService
{
    public ResolvedMetadataTarget ResolveLayout(string repoPath, SalesforceConfigRequirement requirement)
    {
        var layoutDirectory = Path.Combine(repoPath, "force-app", "main", "default", "layouts");
        return ResolveFile(layoutDirectory, "*.layout-meta.xml", requirement, requirement.ObjectApiName);
    }

    public ResolvedMetadataTarget ResolveFlexipage(string repoPath, SalesforceConfigRequirement requirement)
    {
        var flexipageDirectory = Path.Combine(repoPath, "force-app", "main", "default", "flexipages");
        return ResolveFile(flexipageDirectory, "*.flexipage-meta.xml", requirement, requirement.ObjectApiName);
    }

    private static ResolvedMetadataTarget ResolveFile(string directory, string pattern, SalesforceConfigRequirement requirement, string objectPrefix)
    {
        if (!Directory.Exists(directory))
        {
            return ResolvedMetadataTarget.Unsupported($"Metadata directory was not found: {directory}");
        }

        var files = Directory.GetFiles(directory, pattern).ToList();
        if (!string.IsNullOrWhiteSpace(objectPrefix) && pattern.Contains("layout", StringComparison.OrdinalIgnoreCase))
        {
            files = files
                .Where(path => Path.GetFileName(path).StartsWith(objectPrefix + "-", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        else if (!string.IsNullOrWhiteSpace(objectPrefix) && pattern.Contains("flexipage", StringComparison.OrdinalIgnoreCase))
        {
            var objectAliases = NormalizeObjectAliases(objectPrefix);
            var objectNamedFiles = files
                .Where(path =>
                {
                    var normalizedFileName = Normalize(Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path)));
                    return objectAliases.Any(alias => normalizedFileName.Contains(alias, StringComparison.OrdinalIgnoreCase));
                })
                .ToList();

            if (objectNamedFiles.Count > 0)
            {
                files = objectNamedFiles;
            }
        }

        if (files.Count == 0)
        {
            return ResolvedMetadataTarget.Unsupported("No existing metadata files matched the requested object or metadata type.");
        }

        var tokens = BuildSearchTokens(requirement);
        if (tokens.Count == 0)
        {
            return files.Count == 1
                ? ResolvedMetadataTarget.Supported(files[0])
                : ResolvedMetadataTarget.Unsupported("Multiple existing metadata files matched. The request needs a layout/page name or clearer target.");
        }

        var scored = files
            .Select(path => new
            {
                Path = path,
                Score = Score(Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path)), tokens)
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Path)
            .ToList();

        if (scored[0].Score <= 0)
        {
            return files.Count == 1
                ? ResolvedMetadataTarget.Supported(files[0])
                : ResolvedMetadataTarget.Unsupported("No existing metadata file name matched the requested target text.");
        }

        if (scored.Count > 1 && scored[0].Score == scored[1].Score)
        {
            return ResolvedMetadataTarget.Unsupported("Multiple metadata files matched equally. The request needs a more specific target name.");
        }

        return ResolvedMetadataTarget.Supported(scored[0].Path);
    }

    private static List<string> BuildSearchTokens(SalesforceConfigRequirement requirement)
    {
        var raw = string.Join(" ", new[]
        {
            requirement.TargetMetadataName,
            requirement.TargetLayoutOrPageLabel,
            requirement.Label,
            requirement.Description,
            requirement.TargetSectionLabel,
            requirement.TargetRegionOrComponent,
            requirement.ObjectApiName
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        var tokens = Regex.Matches(raw, @"[A-Za-z0-9_]+")
            .Select(match => match.Value)
            .Where(value => value.Length > 2)
            .Where(value => !IgnoredTokens.Contains(value, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

        return tokens;
    }

    private static int Score(string fileName, IReadOnlyList<string> tokens)
    {
        var normalizedFileName = Normalize(fileName);
        return tokens.Count(token => normalizedFileName.Contains(Normalize(token), StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string value)
    {
        return Regex.Replace(value, @"[^A-Za-z0-9]+", string.Empty).ToLowerInvariant();
    }

    private static IReadOnlyList<string> NormalizeObjectAliases(string objectApiName)
    {
        var normalized = Normalize(Regex.Replace(objectApiName, "__c$", string.Empty, RegexOptions.IgnoreCase));
        if (normalized.Equals("account", StringComparison.OrdinalIgnoreCase))
        {
            return new[] { "account", "organisation", "organization" };
        }

        if (normalized.Equals("candidate", StringComparison.OrdinalIgnoreCase))
        {
            return new[] { "candidate", "candidaterevolutionpage" };
        }

        if (normalized.Equals("contact", StringComparison.OrdinalIgnoreCase))
        {
            return new[] { "contact", "contactrevolutionpage" };
        }

        return new[] { normalized };
    }

    private static readonly string[] IgnoredTokens =
    {
        "layout", "page", "field", "fields", "section", "replace", "with", "visible", "visibility", "existing",
        "update", "add", "remove", "object", "record", "details", "when", "only", "blank"
    };
}

public sealed record ResolvedMetadataTarget(bool IsSupported, string FilePath, string Reason)
{
    public static ResolvedMetadataTarget Supported(string filePath) => new(true, filePath, string.Empty);
    public static ResolvedMetadataTarget Unsupported(string reason) => new(false, string.Empty, reason);
}
