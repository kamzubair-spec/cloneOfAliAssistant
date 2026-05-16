using System.Text.RegularExpressions;
using System.Net;
using System.Text;
using eZBERP_AI_IDE.Models;
using File = System.IO.File;

namespace eZBERP_AI_IDE.Services;

public sealed class GlobalValueSetManagementService : IRepositoryAwareConfigWorkItemHandler
{
    public string ServiceName => nameof(GlobalValueSetManagementService);

    public bool CanHandle(SalesforceConfigRequirement requirement)
    {
        return requirement.Type.Equals("global_value_set", StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(GetSetName(requirement))
               && (requirement.PicklistEntries.Count > 0
                   || requirement.PicklistValues.Count > 0
                   || requirement.PicklistRenames.Count > 0);
    }

    public bool CanHandle(string repoPath, SalesforceConfigRequirement requirement)
    {
        return CanHandle(requirement) && File.Exists(ResolvePath(repoPath, requirement));
    }

    public string BuildCannotHandleReason(string repoPath, SalesforceConfigRequirement requirement)
    {
        if (string.IsNullOrWhiteSpace(GetSetName(requirement)))
        {
            return "Global value set changes need a global value set metadata name.";
        }

        if (!File.Exists(ResolvePath(repoPath, requirement)))
        {
            return $"Global value set metadata was not found: {GetSetName(requirement)}";
        }

        if (requirement.PicklistEntries.Count == 0
            && requirement.PicklistValues.Count == 0
            && requirement.PicklistRenames.Count == 0)
        {
            return "Global value set changes need at least one value to add or label to rename.";
        }

        return "Global value set requirement is supported.";
    }

    public async Task<FileChangeSet?> BuildChangeSetAsync(string repoPath, SalesforceConfigRequirement requirement)
    {
        var path = ResolvePath(repoPath, requirement);
        var existing = await File.ReadAllTextAsync(path);
        var proposed = ApplyRenames(existing, requirement);
        proposed = AddValuesInOrder(proposed, requirement);
        var proposals = new List<FileChangeProposal>
        {
            new(Path.GetRelativePath(repoPath, path), existing, proposed, true)
        };

        if (requirement.AddGlobalValueSetValuesToAllRecordTypes)
        {
            proposals.AddRange(await BuildRecordTypeFanOutProposalsAsync(repoPath, requirement));
        }

        return new FileChangeSet(
            $"Global value set metadata change for {GetSetName(requirement)}",
            proposals);
    }

    private static string ApplyRenames(string existing, SalesforceConfigRequirement requirement)
    {
        var proposed = existing;
        foreach (var rename in BuildRenames(requirement))
        {
            var match = FindCustomValueBlock(proposed, rename.CurrentApiValue, rename.CurrentLabel);
            if (!match.Success)
            {
                throw new InvalidOperationException($"Global value set value was not found for label rename: {FirstNonBlank(rename.CurrentApiValue, rename.CurrentLabel)}");
            }

            var block = match.Value;
            var labelMatch = Regex.Match(block, @"<label>(.*?)</label>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var updatedBlock = labelMatch.Success
                ? block[..labelMatch.Index] + $"<label>{EscapeXml(rename.NewLabel)}</label>" + block[(labelMatch.Index + labelMatch.Length)..]
                : block.Insert(block.LastIndexOf("</customValue>", StringComparison.OrdinalIgnoreCase), $"<label>{EscapeXml(rename.NewLabel)}</label>");

            proposed = proposed[..match.Index] + updatedBlock + proposed[(match.Index + match.Length)..];
        }

        return proposed;
    }

    private static string AddValuesInOrder(string existing, SalesforceConfigRequirement requirement)
    {
        if (existing.IndexOf("</GlobalValueSet>", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException("Global value set XML does not contain a closing </GlobalValueSet> tag.");
        }

        var proposed = existing;
        foreach (var entry in BuildEntries(requirement))
        {
            if (FindCustomValueBlock(proposed, entry.ApiValue, entry.Label).Success)
            {
                continue;
            }

            var insertAt = ResolveOrderedInsertionIndex(proposed, entry);
            var lineEnding = GetLineEnding(proposed);
            var indent = DetectCustomValueIndent(proposed);
            var block = $"{indent}<customValue><fullName>{EscapeXml(entry.ApiValue)}</fullName><default>{entry.Default.ToString().ToLowerInvariant()}</default><label>{EscapeXml(entry.Label)}</label></customValue>{lineEnding}";
            proposed = proposed.Insert(insertAt, block);
        }

        return proposed;
    }

    private static int ResolveOrderedInsertionIndex(string existing, PicklistValueRequirement entry)
    {
        var newKey = SortKey(entry);
        foreach (Match match in CustomValueRegex().Matches(existing))
        {
            var current = ToCustomValue(match);
            if (string.Compare(newKey, SortKey(current), StringComparison.OrdinalIgnoreCase) < 0)
            {
                return GetLineStart(existing, match.Index);
            }
        }

        return existing.IndexOf("</GlobalValueSet>", StringComparison.OrdinalIgnoreCase);
    }

    private static List<PicklistValueRequirement> BuildEntries(SalesforceConfigRequirement requirement)
    {
        var entries = requirement.PicklistEntries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.ApiValue) || !string.IsNullOrWhiteSpace(entry.Label))
            .Select(entry => new PicklistValueRequirement
            {
                ApiValue = string.IsNullOrWhiteSpace(entry.ApiValue) ? entry.Label.Trim() : entry.ApiValue.Trim(),
                Label = string.IsNullOrWhiteSpace(entry.Label) ? entry.ApiValue.Trim() : entry.Label.Trim(),
                Default = entry.Default
            })
            .ToList();

        entries.AddRange(requirement.PicklistValues
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => new PicklistValueRequirement { ApiValue = value.Trim(), Label = value.Trim(), Default = false }));

        return entries
            .GroupBy(entry => entry.ApiValue, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(SortKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<List<FileChangeProposal>> BuildRecordTypeFanOutProposalsAsync(string repoPath, SalesforceConfigRequirement requirement)
    {
        var entries = BuildEntries(requirement);
        if (entries.Count == 0)
        {
            return new List<FileChangeProposal>();
        }

        var proposals = new List<FileChangeProposal>();
        foreach (var usage in FindGlobalValueSetFieldUsages(repoPath, GetSetName(requirement)))
        {
            var recordTypeDirectory = Path.Combine(repoPath, "force-app", "main", "default", "objects", usage.ObjectApiName, "recordTypes");
            if (!Directory.Exists(recordTypeDirectory))
            {
                continue;
            }

            foreach (var recordTypePath in Directory.GetFiles(recordTypeDirectory, "*.recordType-meta.xml"))
            {
                var existing = await File.ReadAllTextAsync(recordTypePath);
                var proposed = AddRecordTypePicklistValues(existing, usage.FieldApiName, entries);
                if (!string.Equals(existing, proposed, StringComparison.Ordinal))
                {
                    proposals.Add(new FileChangeProposal(Path.GetRelativePath(repoPath, recordTypePath), existing, proposed, true));
                }
            }
        }

        return proposals;
    }

    private static IEnumerable<GlobalValueSetFieldUsage> FindGlobalValueSetFieldUsages(string repoPath, string globalValueSetName)
    {
        var objectsDirectory = Path.Combine(repoPath, "force-app", "main", "default", "objects");
        if (!Directory.Exists(objectsDirectory))
        {
            yield break;
        }

        foreach (var fieldPath in Directory.GetFiles(objectsDirectory, "*.field-meta.xml", SearchOption.AllDirectories))
        {
            var metadata = File.ReadAllText(fieldPath);
            if (!metadata.Contains($"<valueSetName>{globalValueSetName}</valueSetName>", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fieldsDirectory = Path.GetDirectoryName(fieldPath);
            var objectDirectory = fieldsDirectory is null ? null : Directory.GetParent(fieldsDirectory)?.FullName;
            if (objectDirectory is null)
            {
                continue;
            }

            yield return new GlobalValueSetFieldUsage(
                Path.GetFileName(objectDirectory),
                Path.GetFileName(fieldPath).Replace(".field-meta.xml", string.Empty, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static string AddRecordTypePicklistValues(string existing, string fieldApiName, IReadOnlyList<PicklistValueRequirement> entries)
    {
        var match = FindRecordTypePicklistBlock(existing, fieldApiName);
        if (match.Success)
        {
            var updatedBlock = AddRecordTypeValuesInOrder(match.Value, entries);
            return existing[..match.Index] + updatedBlock + existing[(match.Index + match.Length)..];
        }

        var closingIndex = existing.IndexOf("</RecordType>", StringComparison.OrdinalIgnoreCase);
        if (closingIndex < 0)
        {
            throw new InvalidOperationException("Record type XML does not contain a closing </RecordType> tag.");
        }

        var lineEnding = GetLineEnding(existing);
        var block = BuildRecordTypePicklistBlock(fieldApiName, entries, lineEnding);
        return existing.Insert(closingIndex, block);
    }

    private static string AddRecordTypeValuesInOrder(string picklistBlock, IReadOnlyList<PicklistValueRequirement> entries)
    {
        var proposed = picklistBlock;
        foreach (var entry in entries)
        {
            if (RecordTypeValueExists(proposed, entry.ApiValue))
            {
                continue;
            }

            var insertAt = ResolveRecordTypeValueInsertionIndex(proposed, entry);
            var lineEnding = GetLineEnding(proposed);
            var indent = DetectRecordTypeValueIndent(proposed);
            var valueBlock = $"{indent}<values><fullName>{EscapeXml(entry.ApiValue)}</fullName><default>{entry.Default.ToString().ToLowerInvariant()}</default></values>{lineEnding}";
            proposed = proposed.Insert(insertAt, valueBlock);
        }

        return proposed;
    }

    private static int ResolveRecordTypeValueInsertionIndex(string picklistBlock, PicklistValueRequirement entry)
    {
        var newKey = entry.ApiValue;
        foreach (Match match in Regex.Matches(picklistBlock, @"<values\b[^>]*>.*?</values>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var currentValue = ExtractTag(match.Value, "fullName");
            if (string.Compare(newKey, currentValue, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return GetLineStart(picklistBlock, match.Index);
            }
        }

        return picklistBlock.LastIndexOf("</picklistValues>", StringComparison.OrdinalIgnoreCase);
    }

    private static Match FindRecordTypePicklistBlock(string xml, string fieldApiName)
    {
        foreach (Match match in Regex.Matches(xml, @"<picklistValues\b[^>]*>.*?</picklistValues>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            if (string.Equals(ExtractTag(match.Value, "picklist"), fieldApiName, StringComparison.OrdinalIgnoreCase))
            {
                return match;
            }
        }

        return Match.Empty;
    }

    private static bool RecordTypeValueExists(string picklistBlock, string apiValue)
    {
        foreach (Match match in Regex.Matches(picklistBlock, @"<values\b[^>]*>.*?</values>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            if (string.Equals(ExtractTag(match.Value, "fullName"), apiValue, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildRecordTypePicklistBlock(string fieldApiName, IReadOnlyList<PicklistValueRequirement> entries, string lineEnding)
    {
        var builder = new StringBuilder();
        builder.Append($"    <picklistValues>{lineEnding}");
        builder.Append($"        <picklist>{EscapeXml(fieldApiName)}</picklist>{lineEnding}");
        foreach (var entry in entries.OrderBy(entry => entry.ApiValue, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append($"        <values><fullName>{EscapeXml(entry.ApiValue)}</fullName><default>{entry.Default.ToString().ToLowerInvariant()}</default></values>{lineEnding}");
        }

        builder.Append($"    </picklistValues>{lineEnding}");
        return builder.ToString();
    }

    private static List<PicklistValueRenameRequirement> BuildRenames(SalesforceConfigRequirement requirement)
    {
        return requirement.PicklistRenames
            .Where(rename => !string.IsNullOrWhiteSpace(rename.NewLabel)
                             && (!string.IsNullOrWhiteSpace(rename.CurrentApiValue)
                                 || !string.IsNullOrWhiteSpace(rename.CurrentLabel)))
            .GroupBy(rename => FirstNonBlank(rename.CurrentApiValue, rename.CurrentLabel), StringComparer.OrdinalIgnoreCase)
            .Select(group => new PicklistValueRenameRequirement
            {
                CurrentApiValue = group.First().CurrentApiValue.Trim(),
                CurrentLabel = group.First().CurrentLabel.Trim(),
                NewLabel = group.First().NewLabel.Trim()
            })
            .ToList();
    }

    private static Match FindCustomValueBlock(string xml, string apiValue, string label)
    {
        foreach (Match match in CustomValueRegex().Matches(xml))
        {
            var current = ToCustomValue(match);
            if (!string.IsNullOrWhiteSpace(apiValue)
                && string.Equals(current.ApiValue, apiValue.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return match;
            }

            if (string.IsNullOrWhiteSpace(apiValue)
                && !string.IsNullOrWhiteSpace(label)
                && string.Equals(current.Label, label.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return match;
            }
        }

        return Match.Empty;
    }

    private static PicklistValueRequirement ToCustomValue(Match match)
    {
        return new PicklistValueRequirement
        {
            ApiValue = ExtractTag(match.Value, "fullName"),
            Label = ExtractTag(match.Value, "label"),
            Default = string.Equals(ExtractTag(match.Value, "default"), "true", StringComparison.OrdinalIgnoreCase)
        };
    }

    private static string ExtractTag(string xml, string tagName)
    {
        var match = Regex.Match(xml, $@"<{tagName}>(.*?)</{tagName}>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return match.Success ? DecodeXml(match.Groups[1].Value.Trim()) : string.Empty;
    }

    private static string DecodeXml(string value)
    {
        try
        {
            return WebUtility.HtmlDecode(value);
        }
        catch
        {
            return value;
        }
    }

    private static string ResolvePath(string repoPath, SalesforceConfigRequirement requirement)
        => Path.Combine(repoPath, "force-app", "main", "default", "globalValueSets", $"{GetSetName(requirement)}.globalValueSet-meta.xml");

    private static string GetSetName(SalesforceConfigRequirement requirement) => FirstNonBlank(requirement.TargetMetadataName, requirement.Label, requirement.FieldApiName);
    private static string SortKey(PicklistValueRequirement entry) => FirstNonBlank(entry.Label, entry.ApiValue);
    private static string FirstNonBlank(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    private static string EscapeXml(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
    private static Regex CustomValueRegex() => new(@"<customValue\b[^>]*>.*?</customValue>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static int GetLineStart(string text, int index)
    {
        var previousNewLine = text.LastIndexOf('\n', Math.Max(0, index));
        return previousNewLine < 0 ? 0 : previousNewLine + 1;
    }

    private static string DetectCustomValueIndent(string text)
    {
        var match = Regex.Match(text, @"(?m)^(\s*)<customValue\b", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : "    ";
    }

    private static string DetectRecordTypeValueIndent(string text)
    {
        var match = Regex.Match(text, @"(?m)^(\s*)<values\b", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : "        ";
    }

    private static string GetLineEnding(string text)
        => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private sealed record GlobalValueSetFieldUsage(string ObjectApiName, string FieldApiName);
}
