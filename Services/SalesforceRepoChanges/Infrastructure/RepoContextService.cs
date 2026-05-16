using System.Text;
using System.Text.RegularExpressions;
using eZBERP_AI_IDE.Models;
using File = System.IO.File;

namespace eZBERP_AI_IDE.Services;

public sealed class RepoContextService
{
    public async Task<List<string>> FindRelevantFilesAsync(string repoPath, string storyText)
    {
        var discoveredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        // 1. Extract potential class/object/file names from story
        var candidates = ExtractPotentialSymbols(storyText);

        // 2. Search for these symbols in the file system (exact matches in filenames)
        foreach (var candidate in candidates)
        {
            foreach (var searchPath in GetSalesforceSearchPaths(repoPath))
            {
                if (!Directory.Exists(searchPath)) continue;

                var files = Directory.GetFiles(searchPath, $"*{candidate}*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    if (IsCodeFile(file))
                    {
                        discoveredFiles.Add(Path.GetRelativePath(repoPath, file));
                    }
                }
            }
        }

        ExpandSalesforceCodeContext(repoPath, storyText, discoveredFiles);

        return RankRelevantFiles(discoveredFiles, storyText)
            .Take(12)
            .ToList();
    }

    private static void ExpandSalesforceCodeContext(string repoPath, string storyText, HashSet<string> discoveredFiles)
    {
        ExpandLwcContext(repoPath, discoveredFiles);

        foreach (var objectName in ExtractLikelySObjectNames(storyText))
        {
            AddIfExists(repoPath, discoveredFiles, "triggers", $"{objectName}Trigger.trigger");
            AddIfExists(repoPath, discoveredFiles, "classes", $"{objectName}TriggerHandler.cls");
            AddIfExists(repoPath, discoveredFiles, "classes", $"{objectName}TriggerHandlerTest.cls");
            AddWildcardMatches(repoPath, discoveredFiles, "classes", $"{objectName}*TriggerHandler*.cls");
            AddWildcardMatches(repoPath, discoveredFiles, "classes", $"{objectName}*Service*.cls");
            AddWildcardMatches(repoPath, discoveredFiles, "classes", $"{objectName}*Helper*.cls");
        }

        foreach (var triggerPath in discoveredFiles.Where(file => file.EndsWith(".trigger", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            AddReferencedApexClasses(repoPath, discoveredFiles, triggerPath);
        }

        foreach (var classPath in discoveredFiles.Where(file => file.EndsWith(".cls", StringComparison.OrdinalIgnoreCase) && !IsTestClass(file)).ToList())
        {
            AddReferencedApexClasses(repoPath, discoveredFiles, classPath);
            AddMatchingTests(repoPath, discoveredFiles, classPath);
        }
    }

    private static void ExpandLwcContext(string repoPath, HashSet<string> discoveredFiles)
    {
        foreach (var lwcFile in discoveredFiles.Where(IsLwcPath).ToList())
        {
            var directoryName = Path.GetDirectoryName(lwcFile);
            if (string.IsNullOrWhiteSpace(directoryName))
            {
                continue;
            }

            foreach (var searchRoot in GetSalesforceSearchPaths(repoPath))
            {
                var componentDirectory = Path.Combine(repoPath, directoryName);
                if (!Directory.Exists(componentDirectory))
                {
                    componentDirectory = Path.Combine(searchRoot, Path.GetRelativePath(Path.Combine(repoPath, "force-app", "main", "default"), Path.Combine(repoPath, directoryName)));
                }

                if (!Directory.Exists(componentDirectory))
                {
                    continue;
                }

                foreach (var file in Directory.GetFiles(componentDirectory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (IsCodeFile(file))
                    {
                        discoveredFiles.Add(Path.GetRelativePath(repoPath, file));
                    }
                }
            }
        }
    }

    private static List<string> RankRelevantFiles(IEnumerable<string> files, string storyText)
    {
        var preferLwc = ContainsAny(storyText, "lwc", "lightning web component", "component", ".js", ".html");
        var preferVisualforce = ContainsAny(storyText, "visualforce", ".page");
        var preferAura = ContainsAny(storyText, "aura", ".cmp");

        return files
            .OrderBy(file => GetFileRank(file, preferLwc, preferAura, preferVisualforce))
            .ThenBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int GetFileRank(string file, bool preferLwc, bool preferAura, bool preferVisualforce)
    {
        var name = Path.GetFileName(file);
        if (preferLwc && IsLwcPath(file))
        {
            return 0;
        }

        if (preferAura && file.Contains($"{Path.DirectorySeparatorChar}aura{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (preferVisualforce && file.EndsWith(".page", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (name.Contains("TriggerHandler", StringComparison.OrdinalIgnoreCase) && !IsTestClass(file))
        {
            return 1;
        }

        if (name.Contains("Service", StringComparison.OrdinalIgnoreCase) && !IsTestClass(file))
        {
            return 2;
        }

        if (name.Contains("Helper", StringComparison.OrdinalIgnoreCase) && !IsTestClass(file))
        {
            return 3;
        }

        if (file.EndsWith(".trigger", StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        if (IsLwcPath(file))
        {
            return 5;
        }

        return IsTestClass(file) ? 7 : 6;
    }

    private static IEnumerable<string> ExtractLikelySObjectNames(string text)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in Regex.Matches(text, @"\b(?<name>[A-Z][A-Za-z0-9_]*)(__c)?\b"))
        {
            var name = match.Groups["name"].Value;
            if (IsCommonEnglishWord(name))
            {
                continue;
            }

            if (name.EndsWith("__c", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^3];
            }

            if (name is "Organisation" or "Organization")
            {
                names.Add("Account");
                continue;
            }

            names.Add(name);
        }

        if (ContainsAny(text, "organisation", "organization", "account"))
        {
            names.Add("Account");
        }

        if (ContainsAny(text, "placement"))
        {
            names.Add("Placement");
        }

        if (ContainsAny(text, "supplier"))
        {
            names.Add("Supplier");
        }

        return names;
    }

    private static void AddIfExists(string repoPath, HashSet<string> discoveredFiles, string metadataFolder, string fileName)
    {
        foreach (var searchRoot in GetSalesforceSearchPaths(repoPath))
        {
            var path = Path.Combine(searchRoot, metadataFolder, fileName);
            if (File.Exists(path))
            {
                discoveredFiles.Add(Path.GetRelativePath(repoPath, path));
            }
        }
    }

    private static void AddWildcardMatches(string repoPath, HashSet<string> discoveredFiles, string metadataFolder, string pattern)
    {
        foreach (var searchRoot in GetSalesforceSearchPaths(repoPath))
        {
            var directory = Path.Combine(searchRoot, metadataFolder);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly))
            {
                discoveredFiles.Add(Path.GetRelativePath(repoPath, file));
            }
        }
    }

    private static void AddReferencedApexClasses(string repoPath, HashSet<string> discoveredFiles, string relativePath)
    {
        var path = Path.Combine(repoPath, relativePath);
        if (!File.Exists(path))
        {
            return;
        }

        var content = File.ReadAllText(path);
        foreach (Match match in Regex.Matches(content, @"new\s+(?<class>[A-Za-z][A-Za-z0-9_]*)\s*\("))
        {
            AddIfExists(repoPath, discoveredFiles, "classes", $"{match.Groups["class"].Value}.cls");
        }
    }

    private static void AddMatchingTests(string repoPath, HashSet<string> discoveredFiles, string classPath)
    {
        var className = Path.GetFileNameWithoutExtension(classPath);
        if (string.IsNullOrWhiteSpace(className))
        {
            return;
        }

        AddWildcardMatches(repoPath, discoveredFiles, "classes", $"{className}*Test*.cls");
        AddWildcardMatches(repoPath, discoveredFiles, "classes", $"{className}Test*.cls");
    }

    private static bool IsTestClass(string file)
    {
        return Path.GetFileNameWithoutExtension(file).Contains("Test", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLwcPath(string file)
    {
        var normalized = file.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        return normalized.Contains($"{Path.DirectorySeparatorChar}lwc{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> ExtractPotentialSymbols(string text)
    {
        // 1. Match CamelCase words, snake_case_words, and words ending in __c
        // 2. Also match lowercase words that might be object names
        var matches = Regex.Matches(text, @"\b[A-Za-z_][A-Za-z0-9_]*\b");
        return matches.Cast<Match>()
            .Select(m => m.Value)
            .Where(v => v.Length > 3)
            .Where(v => !IsCommonEnglishWord(v))
            .Distinct()
            .ToList();
    }

    private static bool IsCommonEnglishWord(string word)
    {
        var common = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "this", "that", "with", "from", "should", "could", "would", "then", "there",
            "when", "where", "which", "while", "create", "update", "delete", "modify",
            "field", "object", "class", "trigger", "page", "requirement", "story", "user"
        };
        return common.Contains(word);
    }

    private static bool ContainsAny(string source, params string[] values)
    {
        return values.Any(value => source.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCodeFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".cls" or ".trigger" or ".js" or ".html" or ".css" or ".cmp" or ".page";
    }

    public bool ValidateSalesforceProject(string path)
    {
        return Directory.Exists(Path.Combine(path, "force-app")) || Directory.Exists(Path.Combine(path, "src"));
    }

    public RepoStats GetRepoStats(string path)
    {
        var stats = new RepoStats();

        foreach (var searchPath in GetSalesforceSearchPaths(path))
        {
            if (!Directory.Exists(searchPath))
            {
                continue;
            }

            stats.Classes += CountFilesIfDirectoryExists(Path.Combine(searchPath, "classes"), "*.cls");
            stats.Triggers += CountFilesIfDirectoryExists(Path.Combine(searchPath, "triggers"), "*.trigger");
            stats.Lwc += CountDirectoriesIfExists(Path.Combine(searchPath, "lwc"));
            stats.Aura += CountDirectoriesIfExists(Path.Combine(searchPath, "aura"));
        }

        return stats;
    }

    public SalesforceFiles GetAllSalesforceFiles(string repoPath)
    {
        var files = new SalesforceFiles();

        foreach (var searchPath in GetSalesforceSearchPaths(repoPath))
        {
            if (!Directory.Exists(searchPath))
            {
                continue;
            }

            AddFilesIfDirectoryExists(Path.Combine(searchPath, "classes"), "*.cls", files.Classes);
            AddFilesIfDirectoryExists(Path.Combine(searchPath, "triggers"), "*.trigger", files.Triggers);
            AddDirectoriesIfExists(Path.Combine(searchPath, "lwc"), files.LwcComponents);
            AddDirectoriesIfExists(Path.Combine(searchPath, "aura"), files.AuraComponents);
            AddFilesIfDirectoryExists(Path.Combine(searchPath, "pages"), "*.page", files.Pages);
            AddFilesIfDirectoryExists(Path.Combine(searchPath, "profiles"), "*.profile-meta.xml", files.ProfileFiles);
        }

        return files;
    }

    public async Task<string> ReadFileFromRepoAsync(string repoPath, string filePath)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            return string.Empty;
        }

        filePath = NormalizeRequestedPath(repoPath, filePath);

        var resolvedPath = ResolveRepoPath(repoPath, filePath);
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            var requestedDirectory = GetRequestedDirectory(repoPath, filePath);
            if (!string.IsNullOrWhiteSpace(requestedDirectory) && Directory.Exists(requestedDirectory))
            {
                return await ReadDirectoryContentsAsync(requestedDirectory, $"Requested file not found: {filePath}");
            }

            return string.Empty;
        }

        if (Directory.Exists(resolvedPath))
        {
            return await ReadDirectoryContentsAsync(resolvedPath);
        }

        if (File.Exists(resolvedPath))
        {
            return await File.ReadAllTextAsync(resolvedPath);
        }

        return string.Empty;
    }

    public string BuildRepoStatsSummary(string repoPath)
    {
        var stats = GetRepoStats(repoPath);
        return $"{stats.Classes} classes, {stats.Triggers} triggers, {stats.Lwc} LWC components";
    }

    private static string NormalizeRequestedPath(string repoPath, string filePath)
    {
        filePath = filePath.Replace("repopath", "", StringComparison.OrdinalIgnoreCase);
        filePath = filePath.Trim().TrimStart('/', '\\');

        if (filePath.StartsWith(repoPath, StringComparison.OrdinalIgnoreCase))
        {
            filePath = filePath.Substring(repoPath.Length).TrimStart('/', '\\');
        }

        return filePath;
    }

    private static string? GetRequestedDirectory(string repoPath, string filePath)
    {
        var requestedDirectory = Path.GetDirectoryName(filePath)?.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(requestedDirectory))
        {
            return null;
        }

        var fullRequestedDirectory = Path.Combine(repoPath, requestedDirectory);
        return Directory.Exists(fullRequestedDirectory) ? fullRequestedDirectory : null;
    }

    private static string? ResolveRepoPath(string repoPath, string filePath)
    {
        var directPath = Path.Combine(repoPath, filePath);
        if (File.Exists(directPath) || Directory.Exists(directPath))
        {
            return directPath;
        }

        foreach (var altPath in GetAlternativePaths(repoPath, filePath))
        {
            if (File.Exists(altPath) || Directory.Exists(altPath))
            {
                return altPath;
            }
        }

        var normalizedTarget = NormalizeName(Path.GetFileName(filePath));
        if (string.IsNullOrWhiteSpace(normalizedTarget))
        {
            return null;
        }

        var requestedDirectory = GetRequestedDirectory(repoPath, filePath);

        foreach (var searchRoot in GetSalesforceSearchPaths(repoPath))
        {
            if (!Directory.Exists(searchRoot))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(requestedDirectory))
            {
                var candidate = FindMatchingEntry(requestedDirectory, normalizedTarget);
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    return candidate;
                }
            }

            var fallbackCandidate = FindMatchingEntry(searchRoot, normalizedTarget, true);
            if (!string.IsNullOrWhiteSpace(fallbackCandidate))
            {
                return fallbackCandidate;
            }
        }

        return null;
    }

    private static string? FindMatchingEntry(string directory, string normalizedTarget, bool recursive = false)
    {
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", option))
        {
            if (NormalizeName(Path.GetFileName(entry)) == normalizedTarget)
            {
                return entry;
            }
        }

        return null;
    }

    private static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static IEnumerable<string> GetSalesforceSearchPaths(string repoPath)
    {
        yield return Path.Combine(repoPath, "force-app", "main", "default");
        yield return Path.Combine(repoPath, "src");
    }

    private static IEnumerable<string> GetAlternativePaths(string repoPath, string filePath)
    {
        yield return Path.Combine(repoPath, "force-app", "main", "default", "objects", Path.GetFileName(filePath));
        yield return Path.Combine(repoPath, "force-app", "main", "default", "classes", $"{Path.GetFileName(filePath)}.cls");
        yield return Path.Combine(repoPath, "src", "objects", Path.GetFileName(filePath));
    }

    private static int CountFilesIfDirectoryExists(string directory, string pattern)
    {
        return Directory.Exists(directory) ? Directory.GetFiles(directory, pattern).Length : 0;
    }

    private static int CountDirectoriesIfExists(string directory)
    {
        return Directory.Exists(directory) ? Directory.GetDirectories(directory).Length : 0;
    }

    private static void AddFilesIfDirectoryExists(string directory, string pattern, List<FileInfo> target)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        target.AddRange(Directory.GetFiles(directory, pattern).Select(file => new FileInfo(file)));
    }

    private static void AddDirectoriesIfExists(string directory, List<string> target)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        target.AddRange(
            Directory.GetDirectories(directory)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))!);
    }

    private static async Task<string> ReadDirectoryContentsAsync(string directoryPath, string? headerMessage = null)
    {
        var result = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(headerMessage))
        {
            result.AppendLine(headerMessage);
            result.AppendLine();
        }

        result.AppendLine($"Directory: {directoryPath}");
        result.AppendLine(new string('=', 60));

        var fieldFiles = Directory.GetFiles(directoryPath, "*.field-meta.xml");
        if (fieldFiles.Any())
        {
            result.AppendLine();
            result.AppendLine("FIELD FILES FOUND:");
            foreach (var fieldFile in fieldFiles)
            {
                result.AppendLine($"  - {Path.GetFileName(fieldFile)}");
            }
        }

        var allFiles = Directory.GetFiles(directoryPath);
        result.AppendLine();
        result.AppendLine("ALL FILES:");
        foreach (var file in allFiles)
        {
            result.AppendLine($"  - {Path.GetFileName(file)}");
        }

        var subDirs = Directory.GetDirectories(directoryPath);
        if (subDirs.Any())
        {
            result.AppendLine();
            result.AppendLine("Subdirectories:");
            foreach (var subDir in subDirs)
            {
                result.AppendLine($"  - {Path.GetFileName(subDir)}");
            }
        }

        return await Task.FromResult(result.ToString());
    }
}

public sealed class RepoStats
{
    public int Classes { get; set; }
    public int Triggers { get; set; }
    public int Lwc { get; set; }
    public int Aura { get; set; }
}

public sealed class SalesforceFiles
{
    public List<FileInfo> Classes { get; } = new();
    public List<FileInfo> Triggers { get; } = new();
    public List<string> LwcComponents { get; } = new();
    public List<string> AuraComponents { get; } = new();
    public List<FileInfo> Pages { get; } = new();
    public List<FileInfo> ProfileFiles { get; } = new();
}
