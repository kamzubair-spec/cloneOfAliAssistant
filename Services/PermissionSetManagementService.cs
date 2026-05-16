using System.Text.RegularExpressions;
using eZBERP_AI_IDE.Models;
using File = System.IO.File;

namespace eZBERP_AI_IDE.Services;

public sealed class PermissionSetManagementService : IRepositoryAwareConfigWorkItemHandler
{
    public string ServiceName => nameof(PermissionSetManagementService);

    public bool CanHandle(SalesforceConfigRequirement requirement)
    {
        return requirement.Type.Equals("permission_set", StringComparison.OrdinalIgnoreCase)
               || requirement.Type.Equals("permission_set_fls_update", StringComparison.OrdinalIgnoreCase);
    }

    public bool CanHandle(string repoPath, SalesforceConfigRequirement requirement)
    {
        if (!CanHandle(requirement))
        {
            return false;
        }

        return ResolveTargetPermissionSetCount(repoPath, requirement) > 0;
    }

    public string BuildCannotHandleReason(string repoPath, SalesforceConfigRequirement requirement)
    {
        if (ResolveTargetPermissionSetCount(repoPath, requirement) == 0)
        {
            return "No matching permission set files were found for the requested FLS update.";
        }

        return "Permission set FLS requirement is supported.";
    }

    public async Task<FileChangeSet?> BuildChangeSetAsync(string repoPath, SalesforceConfigRequirement requirement)
    {
        if (string.IsNullOrWhiteSpace(requirement.ObjectApiName) || string.IsNullOrWhiteSpace(requirement.FieldApiName))
        {
            throw new InvalidOperationException("Permission set FLS changes require both objectApiName and fieldApiName.");
        }

        var permissionSetsDirectory = Path.Combine(repoPath, "force-app", "main", "default", "permissionsets");
        if (!Directory.Exists(permissionSetsDirectory))
        {
            throw new DirectoryNotFoundException($"Permission sets directory was not found: {permissionSetsDirectory}");
        }

        var permissionSetPaths = Directory.GetFiles(permissionSetsDirectory, "*.permissionset-meta.xml");
        var targetPaths = ResolvePermissionSetPaths(permissionSetPaths, requirement.PermissionSetNames);
        if (targetPaths.Count == 0 && requirement.ProfileAccess is not null)
        {
            targetPaths = ResolvePermissionSetPaths(permissionSetPaths, requirement.ProfileAccess.EditableProfiles.Concat(requirement.ProfileAccess.ReadOnlyProfiles));
        }

        if (targetPaths.Count == 0)
        {
            throw new InvalidOperationException("No matching permission set files were found for the requested FLS update.");
        }

        var fieldReference = BuildFieldReference(requirement.ObjectApiName, requirement.FieldApiName);
        var proposals = new List<FileChangeProposal>();

        foreach (var path in targetPaths.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(path);
            var isEditable = requirement.ProfileAccess?.EditableProfiles.Any(p => NamesMatch(p, fileName)) ?? false;

            var block = BuildFieldPermissionBlock(fieldReference, isEditable);
            var existingContent = await File.ReadAllTextAsync(path);
            var proposedContent = await Task.Run(() => MergeFieldPermission(existingContent, block));
            proposals.Add(new FileChangeProposal(
                Path.GetRelativePath(repoPath, path),
                existingContent,
                proposedContent,
                true));
        }

        return new FileChangeSet($"Permission set FLS update for {fieldReference}", proposals);
    }

    private static bool NamesMatch(string requestedName, string metadataFileName)
    {
        return NormalizeMetadataName(requestedName) == NormalizeMetadataName(metadataFileName)
               || NormalizeMetadataName(requestedName) == NormalizeMetadataName(Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(metadataFileName)));
    }

    private static int ResolveTargetPermissionSetCount(string repoPath, SalesforceConfigRequirement requirement)
    {
        var permissionSetsDirectory = Path.Combine(repoPath, "force-app", "main", "default", "permissionsets");
        if (!Directory.Exists(permissionSetsDirectory))
        {
            return 0;
        }

        var paths = Directory.GetFiles(permissionSetsDirectory, "*.permissionset-meta.xml");
        var names = requirement.PermissionSetNames;
        if (names.Count == 0 && requirement.ProfileAccess is not null)
        {
            names = requirement.ProfileAccess.EditableProfiles.Concat(requirement.ProfileAccess.ReadOnlyProfiles).ToList();
        }

        return ResolvePermissionSetPaths(paths, names).Count;
    }

    private static HashSet<string> ResolvePermissionSetPaths(IEnumerable<string> paths, IEnumerable<string> requestedNames)
    {
        var available = paths.ToList();
        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var requestedName in requestedNames.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            var normalizedRequest = NormalizeMetadataName(requestedName);
            var match = available.FirstOrDefault(path => NormalizeMetadataName(Path.GetFileName(path)) == normalizedRequest)
                        ?? available.FirstOrDefault(path => NormalizeMetadataName(Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path))) == normalizedRequest);

            if (match is not null)
            {
                resolved.Add(match);
            }
        }

        return resolved;
    }

    private static string NormalizeMetadataName(string value)
    {
        return Regex.Replace(value, @"[\s_\-\.]", string.Empty).ToLowerInvariant()
            .Replace("permissionsetmetaxml", string.Empty)
            .Replace("permissionsetmeta", string.Empty)
            .Replace("permissionset", string.Empty);
    }

    private static string BuildFieldReference(string objectApiName, string fieldApiName)
    {
        var normalizedObject = NormalizeCustomApiName(objectApiName);
        var normalizedField = fieldApiName.Contains("__", StringComparison.OrdinalIgnoreCase)
            ? NormalizeCustomApiName(fieldApiName)
            : NormalizeCustomApiName(fieldApiName.TrimEnd('_') + "__c");

        return $"{normalizedObject}.{normalizedField}";
    }

    private static string NormalizeCustomApiName(string apiName)
    {
        if (string.IsNullOrWhiteSpace(apiName))
        {
            return apiName?.Trim() ?? string.Empty;
        }

        var value = apiName.Trim();
        var suffix = GetCustomFieldSuffix(value);
        if (string.IsNullOrWhiteSpace(suffix))
        {
            return value;
        }

        var baseName = value[..^suffix.Length];
        var parts = baseName
            .Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]);

        return string.Join("_", parts) + suffix.ToLowerInvariant();
    }

    private static string GetCustomFieldSuffix(string value)
    {
        if (value.EndsWith("__pc", StringComparison.OrdinalIgnoreCase))
        {
            return "__pc";
        }

        return value.EndsWith("__c", StringComparison.OrdinalIgnoreCase) ? "__c" : string.Empty;
    }

    private static string BuildFieldPermissionBlock(string fieldReference, bool editable)
    {
        return $"<fieldPermissions><editable>{editable.ToString().ToLowerInvariant()}</editable><field>{fieldReference}</field><readable>true</readable></fieldPermissions>";
    }

    private static string MergeFieldPermission(string existingContent, string newBlock)
    {
        var pattern = @"<fieldPermissions>.*?</fieldPermissions>";
        var existingMatches = Regex.Matches(existingContent, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var newFieldName = ExtractTagValue(newBlock, "field") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(newFieldName))
        {
            throw new InvalidOperationException("The generated fieldPermissions block does not contain a field name.");
        }

        foreach (Match match in existingMatches)
        {
            var existingFieldName = ExtractTagValue(match.Value, "field");
            if (newFieldName.Equals(existingFieldName, StringComparison.OrdinalIgnoreCase))
            {
                return existingContent[..match.Index] + FormatBlockLike(newBlock, match.Value, existingContent, match.Index) + existingContent[(match.Index + match.Length)..];
            }
        }

        foreach (Match match in existingMatches)
        {
            var existingFieldName = ExtractTagValue(match.Value, "field");
            if (!string.IsNullOrWhiteSpace(existingFieldName)
                && string.Compare(newFieldName, existingFieldName, StringComparison.OrdinalIgnoreCase) < 0)
            {
                var lineStart = GetLineStart(existingContent, match.Index);
                return existingContent.Insert(lineStart, BuildInsertionBlock(existingContent, newBlock, match.Value, match.Index) + GetLineEnding(existingContent));
            }
        }

        if (existingMatches.Count > 0)
        {
            var lastMatch = existingMatches[^1];
            var insertIndex = GetNextLineStart(existingContent, lastMatch.Index + lastMatch.Length);
            return existingContent.Insert(insertIndex, BuildInsertionBlock(existingContent, newBlock, lastMatch.Value, lastMatch.Index) + GetLineEnding(existingContent));
        }

        var closingIndex = existingContent.LastIndexOf("</PermissionSet>", StringComparison.OrdinalIgnoreCase);
        if (closingIndex < 0)
        {
            throw new InvalidOperationException("Permission set XML does not contain a closing </PermissionSet> tag.");
        }

        return existingContent.Insert(closingIndex, newBlock + GetLineEnding(existingContent));
    }

    private static string BuildInsertionBlock(string content, string block, string templateBlock, int templateIndex)
    {
        var formatted = FormatBlockLike(block, templateBlock, content, templateIndex);
        return IsSingleLineBlock(templateBlock) ? GetLinePrefix(content, templateIndex) + formatted : formatted;
    }

    private static string FormatBlockLike(string block, string templateBlock, string content, int index)
    {
        if (IsSingleLineBlock(templateBlock))
        {
            return block;
        }

        return IndentBlock(block, DetectIndent(content, index));
    }

    private static string IndentBlock(string block, string indent)
    {
        var lines = block.Replace("\r\n", "\n").Split('\n');
        return string.Join(Environment.NewLine, lines.Select(line => indent + line.Trim()));
    }

    private static bool IsSingleLineBlock(string block)
    {
        return !block.Contains('\n') && !block.Contains('\r');
    }

    private static string? ExtractTagValue(string block, string tagName)
    {
        var match = Regex.Match(block, $@"<{tagName}>(.*?)</{tagName}>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static int GetLineStart(string content, int index)
    {
        var lineStart = content.LastIndexOf('\n', Math.Max(0, index - 1));
        return lineStart < 0 ? 0 : lineStart + 1;
    }

    private static int GetNextLineStart(string content, int index)
    {
        var lineEnd = content.IndexOf('\n', index);
        return lineEnd < 0 ? content.Length : lineEnd + 1;
    }

    private static string GetLinePrefix(string content, int index)
    {
        var lineStart = GetLineStart(content, index);
        return content[lineStart..index];
    }

    private static string GetLineEnding(string content)
    {
        return content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    }

    private static string DetectIndent(string content, int index)
    {
        if (index <= 0 || index > content.Length)
        {
            return "    ";
        }

        var lineStart = content.LastIndexOf('\n', index);
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        var indentLength = 0;

        while (lineStart + indentLength < content.Length)
        {
            var ch = content[lineStart + indentLength];
            if (ch != ' ' && ch != '\t')
            {
                break;
            }

            indentLength++;
        }

        return indentLength > 0 ? content.Substring(lineStart, indentLength) : "    ";
    }
}
