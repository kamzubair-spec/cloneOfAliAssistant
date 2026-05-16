using System.Text.RegularExpressions;
using File = System.IO.File;

namespace eZBERP_AI_IDE.Services;

public sealed class ProfileFlsToolService
{
    public async Task<FileChangeSet> BuildChangeSetAsync(string repoPath, ProfileFlsRequest request)
    {
        var profilesDirectory = Path.Combine(repoPath, "force-app", "main", "default", "profiles");
        if (!Directory.Exists(profilesDirectory))
        {
            throw new DirectoryNotFoundException($"Profiles directory was not found: {profilesDirectory}");
        }

        var profilePaths = Directory.GetFiles(profilesDirectory, "*.profile-meta.xml");
        var editableProfiles = ResolveProfilePaths(profilePaths, request.EditableProfiles);
        var readOnlyProfiles = ResolveProfilePaths(profilePaths, request.ReadOnlyProfiles);

        if (request.ApplyReadOnlyToRemainingProfiles)
        {
            foreach (var profilePath in profilePaths)
            {
                if (!editableProfiles.Contains(profilePath))
                {
                    readOnlyProfiles.Add(profilePath);
                }
            }
        }

        var targetProfiles = editableProfiles
            .Concat(readOnlyProfiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (targetProfiles.Count == 0)
        {
            throw new InvalidOperationException("No matching profile files were found for the requested FLS update.");
        }

        var fieldReference = BuildFieldReference(request.ObjectApiName, request.FieldApiName);
        var proposalTasks = targetProfiles.Select(async profilePath =>
        {
            var existingContent = await File.ReadAllTextAsync(profilePath);
            var editable = editableProfiles.Contains(profilePath);
            var block = BuildFieldPermissionBlock(fieldReference, editable);
            var proposedContent = await Task.Run(() => MergeFieldPermission(existingContent, block));
            var relativePath = Path.GetRelativePath(repoPath, profilePath);

            return new FileChangeProposal(
                relativePath,
                existingContent,
                proposedContent,
                true);
        });

        var proposals = (await Task.WhenAll(proposalTasks))
            .OrderBy(proposal => proposal.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new FileChangeSet($"Profile FLS update for {fieldReference}", proposals);
    }

    private static HashSet<string> ResolveProfilePaths(IEnumerable<string> profilePaths, IEnumerable<string> requestedProfiles)
    {
        var available = profilePaths.ToList();
        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var requestedProfile in requestedProfiles.Where(profile => !string.IsNullOrWhiteSpace(profile)))
        {
            var normalizedRequest = NormalizeProfileName(requestedProfile);
            var match = available.FirstOrDefault(path => NormalizeProfileName(Path.GetFileName(path)) == normalizedRequest);

            if (match is null && !requestedProfile.EndsWith(".profile-meta.xml", StringComparison.OrdinalIgnoreCase))
            {
                match = available.FirstOrDefault(path =>
                    NormalizeProfileName(Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path))) == normalizedRequest);
            }

            if (match is not null)
            {
                resolved.Add(match);
            }
        }

        return resolved;
    }

    private static string NormalizeProfileName(string value)
    {
        return Regex.Replace(value, @"[\s_\-\.]", string.Empty).ToLowerInvariant()
            .Replace("profilemetaxml", string.Empty)
            .Replace("profilemeta", string.Empty)
            .Replace("profile", string.Empty);
    }

    private static string BuildFieldReference(string objectApiName, string fieldApiName)
    {
        var normalizedObject = NormalizeCustomApiName(objectApiName);
        var normalizedField = fieldApiName.Contains("__", StringComparison.OrdinalIgnoreCase)
            ? fieldApiName
            : fieldApiName.TrimEnd('_') + "__c";

        return $"{normalizedObject}.{normalizedField}";
    }

    private static string NormalizeCustomApiName(string apiName)
    {
        var suffix = GetCustomFieldSuffix(apiName);
        if (string.IsNullOrWhiteSpace(suffix))
        {
            return apiName;
        }

        var baseName = apiName[..^suffix.Length];
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
        return $"""
        <fieldPermissions>
            <editable>{editable.ToString().ToLowerInvariant()}</editable>
            <field>{fieldReference}</field>
            <readable>true</readable>
        </fieldPermissions>
        """;
    }

    private static string MergeFieldPermission(string existingContent, string newBlock)
    {
        var fieldPermissionPattern = @"<fieldPermissions>.*?</fieldPermissions>";
        var existingMatches = Regex.Matches(existingContent, fieldPermissionPattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var newFieldName = ExtractFieldName(newBlock);
        if (string.IsNullOrWhiteSpace(newFieldName))
        {
            throw new InvalidOperationException("The generated fieldPermissions block does not contain a field name.");
        }

        foreach (Match match in existingMatches)
        {
            var existingFieldName = ExtractFieldName(match.Value);
            if (newFieldName.Equals(existingFieldName, StringComparison.OrdinalIgnoreCase))
            {
                var replacement = FormatBlockLike(newBlock, match.Value, existingContent, match.Index);
                return existingContent[..match.Index] + replacement + existingContent[(match.Index + match.Length)..];
            }
        }

        foreach (Match match in existingMatches)
        {
            var existingFieldName = ExtractFieldName(match.Value);
            if (!string.IsNullOrWhiteSpace(existingFieldName)
                && string.Compare(newFieldName, existingFieldName, StringComparison.OrdinalIgnoreCase) < 0)
            {
                var lineStart = GetLineStart(existingContent, match.Index);
                var insertion = BuildInsertionBlock(existingContent, newBlock, match.Value, match.Index);
                return existingContent.Insert(lineStart, insertion + GetLineEnding(existingContent));
            }
        }

        if (existingMatches.Count > 0)
        {
            var lastMatch = existingMatches[^1];
            var insertIndex = GetNextLineStart(existingContent, lastMatch.Index + lastMatch.Length);
            var insertion = BuildInsertionBlock(existingContent, newBlock, lastMatch.Value, lastMatch.Index);
            return existingContent.Insert(insertIndex, insertion + GetLineEnding(existingContent));
        }

        var closingIndex = existingContent.LastIndexOf("</Profile>", StringComparison.OrdinalIgnoreCase);
        if (closingIndex < 0)
        {
            throw new InvalidOperationException("Profile XML does not contain a closing </Profile> tag.");
        }

        var blockText = IndentBlock(newBlock, "    ");
        return existingContent.Insert(closingIndex, blockText + GetLineEnding(existingContent));
    }

    private static string BuildInsertionBlock(string content, string block, string templateBlock, int templateIndex)
    {
        var formatted = FormatBlockLike(block, templateBlock, content, templateIndex);
        if (IsSingleLineBlock(templateBlock))
        {
            return GetLinePrefix(content, templateIndex) + formatted;
        }

        return formatted;
    }

    private static string FormatBlockLike(string block, string templateBlock, string content, int index)
    {
        if (IsSingleLineBlock(templateBlock))
        {
            return BuildSingleLineFieldPermissionBlock(
                ExtractTagValue(block, "editable") ?? "false",
                ExtractFieldName(block),
                ExtractTagValue(block, "readable") ?? "true");
        }

        return IndentBlock(block, DetectIndent(content, index));
    }

    private static string BuildSingleLineFieldPermissionBlock(string editable, string fieldName, string readable)
    {
        return $"<fieldPermissions><editable>{editable}</editable><field>{fieldName}</field><readable>{readable}</readable></fieldPermissions>";
    }

    private static bool IsSingleLineBlock(string block)
    {
        return !block.Contains('\n') && !block.Contains('\r');
    }

    private static string ExtractFieldName(string block)
    {
        return ExtractTagValue(block, "field") ?? string.Empty;
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

    private static string IndentBlock(string block, string indent)
    {
        var lines = block.Replace("\r\n", "\n").Split('\n');
        return string.Join(Environment.NewLine, lines.Select(line => indent + line.Trim()));
    }
}

public sealed record ProfileFlsRequest(
    string ObjectApiName,
    string FieldApiName,
    IReadOnlyCollection<string> EditableProfiles,
    IReadOnlyCollection<string> ReadOnlyProfiles,
    bool ApplyReadOnlyToRemainingProfiles);
