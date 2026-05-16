using System.Xml.Linq;
using eZBERP_AI_IDE.Models;
using File = System.IO.File;

namespace eZBERP_AI_IDE.Services;

public sealed class QuickActionManagementService : IRepositoryAwareConfigWorkItemHandler
{
    public string ServiceName => nameof(QuickActionManagementService);

    public bool CanHandle(SalesforceConfigRequirement requirement)
    {
        return IsSupportedRequirementType(requirement)
               && !requirement.Operation.Equals("create", StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(requirement.FieldApiName);
    }

    public bool CanHandle(string repoPath, SalesforceConfigRequirement requirement)
    {
        if (!CanHandle(requirement))
        {
            return false;
        }

        var resolution = ResolveQuickAction(repoPath, requirement);
        if (!resolution.IsSupported)
        {
            return false;
        }

        var content = File.ReadAllText(resolution.FilePath);
        var replaceField = FirstNonBlank(requirement.ReplaceFieldApiName, requirement.ExistingFieldApiName);

        return string.IsNullOrWhiteSpace(replaceField)
            ? !QuickActionContainsField(content, requirement.FieldApiName)
            : QuickActionContainsField(content, replaceField);
    }

    public string BuildCannotHandleReason(string repoPath, SalesforceConfigRequirement requirement)
    {
        if (requirement.Operation.Equals("create", StringComparison.OrdinalIgnoreCase))
        {
            return "Creating new quick actions is outside the current scope. Only existing quick action layouts can be updated.";
        }

        var resolution = ResolveQuickAction(repoPath, requirement);
        if (!resolution.IsSupported)
        {
            return resolution.Reason;
        }

        var replaceField = FirstNonBlank(requirement.ReplaceFieldApiName, requirement.ExistingFieldApiName);
        if (!string.IsNullOrWhiteSpace(replaceField)
            && !QuickActionContainsField(File.ReadAllText(resolution.FilePath), replaceField))
        {
            return $"The field to replace was not found in the quick action layout: {replaceField}";
        }

        return "Quick action requirement is supported.";
    }

    public async Task<FileChangeSet?> BuildChangeSetAsync(string repoPath, SalesforceConfigRequirement requirement)
    {
        var resolution = ResolveQuickAction(repoPath, requirement);
        if (!resolution.IsSupported)
        {
            throw new InvalidOperationException(resolution.Reason);
        }

        var existingContent = await File.ReadAllTextAsync(resolution.FilePath);
        var proposedContent = ApplyQuickActionChange(existingContent, requirement);

        return new FileChangeSet(
            $"Quick action metadata change for {Path.GetFileName(resolution.FilePath)}",
            new[] { new FileChangeProposal(Path.GetRelativePath(repoPath, resolution.FilePath), existingContent, proposedContent, true) });
    }

    private static bool IsSupportedRequirementType(SalesforceConfigRequirement requirement)
    {
        if (requirement.Type.Equals("quick_action", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!requirement.Type.Equals("layout", StringComparison.OrdinalIgnoreCase)
            && !requirement.Type.Equals("flexipage", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(FirstNonBlank(requirement.ReplaceFieldApiName, requirement.ExistingFieldApiName))
               && GetSectionHints(requirement).Any();
    }

    private static ResolvedQuickAction ResolveQuickAction(string repoPath, SalesforceConfigRequirement requirement)
    {
        var quickActionsPath = Path.Combine(repoPath, "force-app", "main", "default", "quickActions");
        if (!Directory.Exists(quickActionsPath))
        {
            return ResolvedQuickAction.Unsupported("No quickActions metadata folder was found in the selected repo.");
        }

        var files = Directory.GetFiles(quickActionsPath, "*.quickAction-meta.xml");
        if (files.Length == 0)
        {
            return ResolvedQuickAction.Unsupported("No quick action metadata files were found.");
        }

        var targetMetadataName = NormalizeQuickActionName(requirement.TargetMetadataName);
        if (!string.IsNullOrWhiteSpace(targetMetadataName))
        {
            var exactPath = Path.Combine(quickActionsPath, targetMetadataName + ".quickAction-meta.xml");
            if (File.Exists(exactPath))
            {
                return ResolvedQuickAction.Supported(exactPath);
            }
        }

        var flexipageResolution = ResolveRelatedRecordQuickActionFromFlexipage(repoPath, quickActionsPath, requirement);
        if (flexipageResolution.IsSupported)
        {
            return flexipageResolution;
        }

        var objectName = NormalizeObjectName(requirement.ObjectApiName);
        var title = FirstNonBlank(requirement.TargetLayoutOrPageLabel, requirement.TargetSectionLabel, requirement.TargetMetadataName);
        if (!string.IsNullOrWhiteSpace(title))
        {
            var labelMatch = files.FirstOrDefault(path =>
                (string.IsNullOrWhiteSpace(objectName) || Path.GetFileName(path).StartsWith(objectName + ".", StringComparison.OrdinalIgnoreCase))
                && QuickActionLabelMatches(path, title));

            if (labelMatch is not null)
            {
                return ResolvedQuickAction.Supported(labelMatch);
            }
        }

        if (!string.IsNullOrWhiteSpace(objectName))
        {
            var objectMatches = files
                .Where(path => Path.GetFileName(path).StartsWith(objectName + ".", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (objectMatches.Count == 1)
            {
                return ResolvedQuickAction.Supported(objectMatches[0]);
            }
        }

        return flexipageResolution.HasDiagnostic
            ? flexipageResolution
            : ResolvedQuickAction.Unsupported("No existing quick action metadata file could be resolved for this requirement.");
    }

    private static ResolvedQuickAction ResolveRelatedRecordQuickActionFromFlexipage(
        string repoPath,
        string quickActionsPath,
        SalesforceConfigRequirement requirement)
    {
        var flexipagesPath = Path.Combine(repoPath, "force-app", "main", "default", "flexipages");
        if (!Directory.Exists(flexipagesPath))
        {
            return ResolvedQuickAction.Unsupported("No flexipages metadata folder was found to resolve related-record quick actions.");
        }

        var sectionHints = GetSectionHints(requirement).ToList();
        if (sectionHints.Count == 0)
        {
            return ResolvedQuickAction.Unsupported("No section/title hint was provided to resolve a related-record quick action from a flexipage.");
        }

        var oldField = FirstNonBlank(requirement.ReplaceFieldApiName, requirement.ExistingFieldApiName);
        var candidateDiagnostics = new List<string>();

        foreach (var flexipagePath in Directory.GetFiles(flexipagesPath, "*.flexipage-meta.xml"))
        {
            XDocument document;
            try
            {
                document = XDocument.Parse(File.ReadAllText(flexipagePath), LoadOptions.PreserveWhitespace);
            }
            catch
            {
                continue;
            }

            var ns = document.Root?.Name.Namespace ?? XNamespace.None;
            foreach (var component in document.Descendants(ns + "componentInstance"))
            {
                var componentName = component.Element(ns + "componentName")?.Value ?? string.Empty;
                if (!componentName.Equals("console:relatedRecord", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var properties = GetComponentProperties(component, ns);
                if (!properties.TryGetValue("titleFieldName", out var titleFieldName)
                    || !MatchesAnyHint(titleFieldName, sectionHints))
                {
                    continue;
                }

                if (!properties.TryGetValue("updateQuickActionName", out var quickActionName)
                    || string.IsNullOrWhiteSpace(quickActionName))
                {
                    candidateDiagnostics.Add($"A related-record component titled '{titleFieldName}' was found, but it does not declare updateQuickActionName.");
                    continue;
                }

                var quickActionPath = Path.Combine(quickActionsPath, NormalizeQuickActionName(quickActionName) + ".quickAction-meta.xml");
                if (!File.Exists(quickActionPath))
                {
                    candidateDiagnostics.Add($"Flexipage component '{titleFieldName}' points to quick action '{quickActionName}', but that metadata file was not found.");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(oldField)
                    && !QuickActionContainsField(File.ReadAllText(quickActionPath), oldField))
                {
                    candidateDiagnostics.Add($"Flexipage component '{titleFieldName}' resolved to quick action '{quickActionName}', but field '{oldField}' was not found there.");
                    continue;
                }

                return ResolvedQuickAction.Supported(quickActionPath);
            }
        }

        return candidateDiagnostics.Count > 0
            ? ResolvedQuickAction.Unsupported(string.Join(" ", candidateDiagnostics), hasDiagnostic: true)
            : ResolvedQuickAction.Unsupported("No matching flexipage related-record component with an update quick action was found for the requested section/title.");
    }

    private static IReadOnlyDictionary<string, string> GetComponentProperties(XElement component, XNamespace ns)
    {
        return component.Elements(ns + "componentInstanceProperties")
            .Select(property => new
            {
                Name = property.Element(ns + "name")?.Value?.Trim() ?? string.Empty,
                Value = property.Element(ns + "value")?.Value?.Trim() ?? string.Empty
            })
            .Where(property => !string.IsNullOrWhiteSpace(property.Name))
            .GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> GetSectionHints(SalesforceConfigRequirement requirement)
    {
        foreach (var value in new[]
                 {
                     requirement.TargetSectionLabel,
                     requirement.TargetLayoutOrPageLabel,
                     requirement.TargetRegionOrComponent,
                     requirement.TargetMetadataName
                 })
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value.Trim();
            }
        }

        foreach (var knownTitle in ExtractQuotedHints(requirement.Description)
                     .Concat(ExtractQuotedHints(requirement.Label))
                     .Where(value => value.Contains("details", StringComparison.OrdinalIgnoreCase)
                                     || value.Contains("section", StringComparison.OrdinalIgnoreCase)))
        {
            yield return knownTitle;
        }
    }

    private static IEnumerable<string> ExtractQuotedHints(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        var start = -1;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '"' || value[i] == '\'' || value[i] == '\u201C' || value[i] == '\u201D')
            {
                if (start < 0)
                {
                    start = i + 1;
                    continue;
                }

                var hint = value[start..i].Trim();
                if (!string.IsNullOrWhiteSpace(hint))
                {
                    yield return hint;
                }

                start = -1;
            }
        }
    }

    private static bool MatchesAnyHint(string value, IEnumerable<string> hints)
    {
        return hints.Any(hint => TextMatches(value, hint));
    }

    private static bool TextMatches(string left, string right)
    {
        var normalizedLeft = NormalizeComparisonText(left);
        var normalizedRight = NormalizeComparisonText(right);
        return normalizedLeft.Equals(normalizedRight, StringComparison.OrdinalIgnoreCase)
               || normalizedLeft.Contains(normalizedRight, StringComparison.OrdinalIgnoreCase)
               || normalizedRight.Contains(normalizedLeft, StringComparison.OrdinalIgnoreCase);
    }

    private static string ApplyQuickActionChange(string existingContent, SalesforceConfigRequirement requirement)
    {
        var document = XDocument.Parse(existingContent, LoadOptions.PreserveWhitespace);
        var ns = document.Root?.Name.Namespace ?? XNamespace.None;
        var fieldName = requirement.FieldApiName;
        var replaceFieldName = FirstNonBlank(requirement.ReplaceFieldApiName, requirement.ExistingFieldApiName);

        if (!string.IsNullOrWhiteSpace(replaceFieldName))
        {
            var field = document.Descendants(ns + "field")
                .FirstOrDefault(element => FieldNamesMatch(element.Value, replaceFieldName));

            if (field is null)
            {
                throw new InvalidOperationException($"The field to replace was not found in the quick action layout: {replaceFieldName}");
            }

            field.Value = fieldName;
        }
        else
        {
            AddField(document, ns, fieldName);
        }

        return Serialize(document, existingContent);
    }

    private static void AddField(XDocument document, XNamespace ns, string fieldName)
    {
        if (document.Descendants(ns + "field").Any(element => element.Value.Equals(fieldName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var column = document.Descendants(ns + "quickActionLayoutColumns").FirstOrDefault()
                     ?? throw new InvalidOperationException("The quick action layout has no quickActionLayoutColumns node.");

        column.Add(
            new XElement(ns + "quickActionLayoutItems",
                new XElement(ns + "emptySpace", "false"),
                new XElement(ns + "field", fieldName),
                new XElement(ns + "uiBehavior", "Edit")));
    }

    private static bool QuickActionContainsField(string content, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return false;
        }

        var document = XDocument.Parse(content, LoadOptions.PreserveWhitespace);
        var ns = document.Root?.Name.Namespace ?? XNamespace.None;
        return document.Descendants(ns + "field")
            .Any(element => FieldNamesMatch(element.Value, fieldName));
    }

    private static bool FieldNamesMatch(string metadataFieldName, string requestedFieldName)
    {
        if (string.IsNullOrWhiteSpace(metadataFieldName) || string.IsNullOrWhiteSpace(requestedFieldName))
        {
            return false;
        }

        var normalizedMetadata = NormalizeFieldNameForComparison(metadataFieldName);
        var normalizedRequested = NormalizeFieldNameForComparison(requestedFieldName);

        return normalizedMetadata.Equals(normalizedRequested, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeFieldNameForComparison(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var clean = System.Text.RegularExpressions.Regex.Replace(value, @"(?i)__c$", "");
        clean = clean.Replace("_", " ");
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"[^A-Za-z0-9]+", " ");
        return clean.Trim().ToLowerInvariant().Replace("  ", " ");
    }

    private static bool QuickActionLabelMatches(string filePath, string title)
    {
        var document = XDocument.Parse(File.ReadAllText(filePath), LoadOptions.PreserveWhitespace);
        var ns = document.Root?.Name.Namespace ?? XNamespace.None;
        var label = document.Root?.Element(ns + "label")?.Value ?? string.Empty;
        return TextMatches(label, title);
    }

    private static string NormalizeQuickActionName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim()
            .Replace(".quickAction-meta.xml", string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeObjectName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim() switch
        {
            "Organisation" => "Account",
            "Organization" => "Account",
            "Account" => "Account",
            _ => value.Trim()
        };
    }

    private static string NormalizeComparisonText(string value)
    {
        return (value ?? string.Empty)
            .Replace("Organisation", "Organization", StringComparison.OrdinalIgnoreCase)
            .Replace("_", " ", StringComparison.Ordinal)
            .Replace("-", " ", StringComparison.Ordinal)
            .Trim();
    }

    private static string Serialize(XDocument document, string originalContent)
    {
        var declaration = document.Declaration?.ToString();
        var body = document.ToString(SaveOptions.DisableFormatting);
        var lineEnding = originalContent.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return string.IsNullOrWhiteSpace(declaration) ? body : declaration + lineEnding + body;
    }

    private static string FirstNonBlank(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private sealed class ResolvedQuickAction
    {
        public bool IsSupported { get; private init; }
        public string FilePath { get; private init; } = string.Empty;
        public string Reason { get; private init; } = string.Empty;
        public bool HasDiagnostic { get; private init; }

        public static ResolvedQuickAction Supported(string filePath)
        {
            return new ResolvedQuickAction { IsSupported = true, FilePath = filePath };
        }

        public static ResolvedQuickAction Unsupported(string reason, bool hasDiagnostic = false)
        {
            return new ResolvedQuickAction { IsSupported = false, Reason = reason, HasDiagnostic = hasDiagnostic };
        }
    }
}


