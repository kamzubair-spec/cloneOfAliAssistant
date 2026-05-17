using System.Xml.Linq;
using eZBERP_AI_IDE.Models;

namespace eZBERP_AI_IDE.Services;

public sealed class MetadataDiscoveryService
{
    public List<string> GetObjectNames(string repoPath)
    {
        var objectsDir = Path.Combine(repoPath, "force-app", "main", "default", "objects");
        if (!Directory.Exists(objectsDir))
        {
            return new List<string>();
        }

        return Directory.GetDirectories(objectsDir)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    public bool ObjectExists(string repoPath, string objectApiName)
    {
        return Directory.Exists(GetObjectDirectory(repoPath, objectApiName));
    }

    public List<string> GetFieldNames(string repoPath, string objectApiName)
    {
        var fieldDir = Path.Combine(GetObjectDirectory(repoPath, objectApiName), "fields");
        if (!Directory.Exists(fieldDir))
        {
            return new List<string>();
        }

        return Directory.GetFiles(fieldDir, "*.field-meta.xml")
            .Select(Path.GetFileNameWithoutExtension)
            .Select(name => name?.Replace(".field-meta", string.Empty, StringComparison.OrdinalIgnoreCase) ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public List<string> GetRecordTypeNames(string repoPath, string objectApiName)
    {
        var recordTypeDir = Path.Combine(GetObjectDirectory(repoPath, objectApiName), "recordTypes");
        if (!Directory.Exists(recordTypeDir))
        {
            return new List<string>();
        }

        return Directory.GetFiles(recordTypeDir, "*.recordType-meta.xml")
            .Select(Path.GetFileNameWithoutExtension)
            .Select(name => name?.Replace(".recordType-meta", string.Empty, StringComparison.OrdinalIgnoreCase) ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public List<string> GetGlobalValueSetNames(string repoPath)
    {
        var dir = Path.Combine(repoPath, "force-app", "main", "default", "globalValueSets");
        if (!Directory.Exists(dir))
        {
            return new List<string>();
        }

        return Directory.GetFiles(dir, "*.globalValueSet-meta.xml")
            .Select(Path.GetFileNameWithoutExtension)
            .Select(name => name?.Replace(".globalValueSet-meta", string.Empty, StringComparison.OrdinalIgnoreCase) ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public List<string> GetGlobalValueSetValues(string repoPath, string globalValueSetName)
    {
        var path = Path.Combine(repoPath, "force-app", "main", "default", "globalValueSets", $"{globalValueSetName}.globalValueSet-meta.xml");
        if (!File.Exists(path))
        {
            return new List<string>();
        }

        var doc = XDocument.Load(path);
        XNamespace ns = "http://soap.sforce.com/2006/04/metadata";
        return doc.Root?
            .Elements(ns + "customValue")
            .Select(value => value.Element(ns + "fullName")?.Value ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList() ?? new List<string>();
    }

    public List<string> GetProfileNames(string repoPath)
    {
        return GetMetadataNames(repoPath, "profiles", "*.profile-meta.xml", ".profile-meta");
    }

    public List<string> GetPermissionSetNames(string repoPath)
    {
        return GetMetadataNames(repoPath, "permissionsets", "*.permissionset-meta.xml", ".permissionset-meta");
    }

    public List<ResolutionOption> FindObjectCandidates(string repoPath, string searchText, int maxResults = 8)
    {
        return RankCandidates(GetObjectNames(repoPath), searchText, "Object", maxResults);
    }

    public List<ResolutionOption> FindProfileCandidates(string repoPath, string searchText, int maxResults = 8)
    {
        return RankCandidates(GetProfileNames(repoPath), searchText, "Profile", maxResults);
    }

    public List<ResolutionOption> FindPermissionSetCandidates(string repoPath, string searchText, int maxResults = 8)
    {
        return RankCandidates(GetPermissionSetNames(repoPath), searchText, "Permission Set", maxResults);
    }

    public List<ResolutionOption> FindGlobalValueSetCandidates(string repoPath, string searchText, int maxResults = 8)
    {
        return RankCandidates(GetGlobalValueSetNames(repoPath), searchText, "Global Value Set", maxResults);
    }

    public string GetObjectDirectory(string repoPath, string objectApiName)
    {
        return Path.Combine(repoPath, "force-app", "main", "default", "objects", objectApiName);
    }

    private static List<string> GetMetadataNames(string repoPath, string folder, string pattern, string suffixToTrim)
    {
        var dir = Path.Combine(repoPath, "force-app", "main", "default", folder);
        if (!Directory.Exists(dir))
        {
            return new List<string>();
        }

        return Directory.GetFiles(dir, pattern)
            .Select(Path.GetFileNameWithoutExtension)
            .Select(name => name?.Replace(suffixToTrim, string.Empty, StringComparison.OrdinalIgnoreCase) ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<ResolutionOption> RankCandidates(IEnumerable<string> names, string searchText, string type, int maxResults)
    {
        var normalizedNeedle = Normalize(searchText);
        return names
            .Select(name => new { Name = name, Score = Score(name, normalizedNeedle) })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .Select(item => new ResolutionOption
            {
                Id = item.Name,
                Label = item.Name,
                Type = type,
                ConfidenceScore = item.Score,
                Description = BuildDescription(type, item.Score, searchText)
            })
            .ToList();
    }

    private static int Score(string candidate, string normalizedNeedle)
    {
        if (string.IsNullOrWhiteSpace(normalizedNeedle))
        {
            return 0;
        }

        var normalizedCandidate = Normalize(candidate);
        if (normalizedCandidate.Equals(normalizedNeedle, StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        if (normalizedCandidate.Contains(normalizedNeedle, StringComparison.OrdinalIgnoreCase))
        {
            return 80;
        }

        var needleWords = normalizedNeedle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (needleWords.Length == 0)
        {
            return 0;
        }

        var hits = needleWords.Count(word => normalizedCandidate.Contains(word, StringComparison.OrdinalIgnoreCase));
        return hits == 0 ? 0 : 40 + hits * 10;
    }

    private static string Normalize(string value)
    {
        return (value ?? string.Empty)
            .Replace("_", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("__c", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static string BuildDescription(string type, int score, string searchText)
    {
        var confidence = score >= 90
            ? "High confidence"
            : score >= 75
                ? "Medium confidence"
                : "Low confidence";

        return string.IsNullOrWhiteSpace(searchText)
            ? $"{type} match. {confidence}."
            : $"{type} match for '{searchText}'. {confidence}.";
    }
}
