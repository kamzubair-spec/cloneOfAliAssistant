using System.Text.RegularExpressions;
using eZBERP_AI_IDE.Models;
using File = System.IO.File;

namespace eZBERP_AI_IDE.Services;

internal sealed class SalesforcePermissionEditingToolkit
{
    internal async Task<List<string>> ResolvePermissionSetPathsAsync(string repoPath, SalesforceConfigRequirement requirement)
    {
        var baseDir = Path.Combine(repoPath, "force-app", "main", "default", "permissionsets");
        if (!Directory.Exists(baseDir))
        {
            Directory.CreateDirectory(baseDir);
        }

        var names = requirement.PermissionSetNames
            .Concat(string.IsNullOrWhiteSpace(requirement.TargetMetadataName) ? Enumerable.Empty<string>() : new[] { requirement.TargetMetadataName })
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return await Task.FromResult(names
            .Select(name => ResolveMetadataPath(baseDir, name, ".permissionset-meta.xml", preferUnderscoreVariant: true))
            .Distinct()
            .ToList());
    }

    internal async Task<List<string>> ResolveProfilePathsAsync(string repoPath, SalesforceConfigRequirement requirement)
    {
        var baseDir = Path.Combine(repoPath, "force-app", "main", "default", "profiles");
        if (!Directory.Exists(baseDir))
        {
            Directory.CreateDirectory(baseDir);
        }

        var profileNames = requirement.ProfileAccess?.EditableProfiles
            .Concat(requirement.ProfileAccess?.ReadOnlyProfiles ?? Enumerable.Empty<string>())
            .ToList() ?? new List<string>();

        if (!string.IsNullOrWhiteSpace(requirement.TargetMetadataName))
        {
            profileNames.Add(requirement.TargetMetadataName);
        }

        if (requirement.ProfileAccess?.ApplyReadOnlyToRemainingProfiles == true)
        {
            return await Task.FromResult(Directory.GetFiles(baseDir, "*.profile-meta.xml").Distinct().ToList());
        }

        return await Task.FromResult(profileNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => ResolveMetadataPath(baseDir, name, ".profile-meta.xml", preferUnderscoreVariant: false))
            .Distinct()
            .ToList());
    }

    internal async Task<FileChangeSet?> BuildCustomPermissionChangeSetAsync(string repoPath, SalesforceConfigRequirement requirement)
    {
        var name = string.IsNullOrWhiteSpace(requirement.TargetMetadataName) ? requirement.Label : requirement.TargetMetadataName;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var customPermissionDirectory = Path.Combine(repoPath, "force-app", "main", "default", "customPermissions");
        Directory.CreateDirectory(customPermissionDirectory);
        var path = ResolveMetadataPath(customPermissionDirectory, name, ".customPermission-meta.xml", preferUnderscoreVariant: true);
        var relativePath = Path.GetRelativePath(repoPath, path);

        var existing = File.Exists(path) ? await File.ReadAllTextAsync(path) : string.Empty;
        var proposed = BuildCustomPermissionContent(existing, requirement, name);

        return new FileChangeSet(
            $"Custom permission {name}",
            new[] { new FileChangeProposal(relativePath, existing, proposed, File.Exists(path)) });
    }

    internal string BuildPermissionSetDefaultXml(SalesforceConfigRequirement requirement, string path)
    {
        var label = string.IsNullOrWhiteSpace(requirement.Label)
            ? BuildFallbackLabelFromFileName(path, ".permissionset-meta.xml")
            : requirement.Label;
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<PermissionSet xmlns=\"http://soap.sforce.com/2006/04/metadata\">\n<label>" + label + "</label>\n</PermissionSet>";
    }

    internal string BuildProfileDefaultXml()
    {
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Profile xmlns=\"http://soap.sforce.com/2006/04/metadata\">\n</Profile>";
    }

    internal string ProcessSurgicalEdit(string content, SalesforceConfigRequirement requirement)
    {
        var tagInfo = GetTagInfo(requirement);
        if (tagInfo == null)
        {
            return content;
        }

        var newBlock = BuildBlock(tagInfo, requirement);
        return MergeBlock(content, newBlock, tagInfo);
    }

    internal SalesforceConfigRequirement ScopeProfileRequirementForTarget(string path, SalesforceConfigRequirement requirement)
    {
        var profileAccess = requirement.ProfileAccess;
        if (profileAccess == null)
        {
            return requirement;
        }

        var profileName = Path.GetFileName(path).Replace(".profile-meta.xml", string.Empty, StringComparison.OrdinalIgnoreCase);
        var editable = profileAccess.EditableProfiles.Any(name => name.Equals(profileName, StringComparison.OrdinalIgnoreCase));
        var readOnly = profileAccess.ReadOnlyProfiles.Any(name => name.Equals(profileName, StringComparison.OrdinalIgnoreCase));
        var applyReadOnlyFallback = profileAccess.ApplyReadOnlyToRemainingProfiles && !editable;

        if (!editable && !readOnly && !applyReadOnlyFallback)
        {
            return requirement;
        }

        return CloneRequirement(requirement, editable ? "true" : "false", profileName);
    }

    private string ResolveMetadataPath(string directory, string metadataName, string suffix, bool preferUnderscoreVariant)
    {
        foreach (var candidate in BuildCandidateFileNames(metadataName, suffix, preferUnderscoreVariant))
        {
            var path = Path.Combine(directory, candidate);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return Path.Combine(directory, BuildCandidateFileNames(metadataName, suffix, preferUnderscoreVariant).First());
    }

    private static IEnumerable<string> BuildCandidateFileNames(string metadataName, string suffix, bool preferUnderscoreVariant)
    {
        var trimmed = metadataName.Trim();
        var exact = trimmed + suffix;
        var underscore = trimmed.Replace(" ", "_") + suffix;
        var space = trimmed.Replace("_", " ") + suffix;

        if (preferUnderscoreVariant)
        {
            yield return underscore;
            if (!exact.Equals(underscore, StringComparison.OrdinalIgnoreCase))
            {
                yield return exact;
            }

            if (!space.Equals(underscore, StringComparison.OrdinalIgnoreCase) && !space.Equals(exact, StringComparison.OrdinalIgnoreCase))
            {
                yield return space;
            }

            yield break;
        }

        yield return exact;
        if (!space.Equals(exact, StringComparison.OrdinalIgnoreCase))
        {
            yield return space;
        }

        if (!underscore.Equals(exact, StringComparison.OrdinalIgnoreCase) && !underscore.Equals(space, StringComparison.OrdinalIgnoreCase))
        {
            yield return underscore;
        }
    }

    private static string BuildCustomPermissionContent(string existing, SalesforceConfigRequirement requirement, string name)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return $"""
<?xml version="1.0" encoding="UTF-8"?>
<CustomPermission xmlns="http://soap.sforce.com/2006/04/metadata">
    <description>{EscapeXml(requirement.Description)}</description>
    <isLicensed>false</isLicensed>
    <label>{EscapeXml(string.IsNullOrWhiteSpace(requirement.Label) ? name : requirement.Label)}</label>
</CustomPermission>
""";
        }

        var updated = ReplaceOrInsertSimpleTag(existing, "description", requirement.Description);
        updated = ReplaceOrInsertSimpleTag(updated, "isLicensed", "false");
        updated = ReplaceOrInsertSimpleTag(updated, "label", string.IsNullOrWhiteSpace(requirement.Label) ? name : requirement.Label);
        return updated;
    }

    private static string ReplaceOrInsertSimpleTag(string content, string tagName, string value)
    {
        var escapedValue = EscapeXml(value ?? string.Empty);
        var replacement = $"    <{tagName}>{escapedValue}</{tagName}>";
        var pattern = $@"^\s*<{tagName}>.*?</{tagName}>\s*$";

        if (Regex.IsMatch(content, pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            return Regex.Replace(content, pattern, replacement, RegexOptions.Multiline | RegexOptions.IgnoreCase);
        }

        var closingTagIndex = content.IndexOf("</CustomPermission>", StringComparison.OrdinalIgnoreCase);
        if (closingTagIndex < 0)
        {
            return content;
        }

        var orderedTags = new[] { "description", "isLicensed", "label" };
        var insertBeforeTag = orderedTags
            .SkipWhile(tag => !tag.Equals(tagName, StringComparison.OrdinalIgnoreCase))
            .Skip(1)
            .Select(tag => content.IndexOf($"<{tag}>", StringComparison.OrdinalIgnoreCase))
            .Where(index => index >= 0)
            .DefaultIfEmpty(closingTagIndex)
            .Min();

        return content.Insert(insertBeforeTag, replacement + Environment.NewLine);
    }

    private record TagInfo(string OuterTag, string KeyTag, string? ValueTag);

    private static TagInfo? GetTagInfo(SalesforceConfigRequirement requirement)
    {
        var type = requirement.PermissionType?.ToLowerInvariant() ?? string.Empty;
        if (requirement.Type.Equals("profile_fls_update", StringComparison.OrdinalIgnoreCase)
            || requirement.Type.Equals("permission_set_fls_update", StringComparison.OrdinalIgnoreCase))
        {
            type = "fls";
        }

        return type switch
        {
            "fls" => new TagInfo("fieldPermissions", "field", null),
            "tab" => new TagInfo("tabVisibilities", "tab", "visibility"),
            "apex_class" => new TagInfo("classAccesses", "apexClass", "enabled"),
            "object" => new TagInfo("objectPermissions", "object", null),
            "custom_permission" => new TagInfo("customPermissions", "name", "enabled"),
            "apex_page" => new TagInfo("pageAccesses", "apexPage", "enabled"),
            "record_type" => new TagInfo("recordTypeVisibilities", "recordType", "visible"),
            "application" => new TagInfo("applicationVisibilities", "application", "visible"),
            "user_permission" => new TagInfo("userPermissions", "name", "enabled"),
            _ => null
        };
    }

    private static string BuildBlock(TagInfo info, SalesforceConfigRequirement requirement)
    {
        var key = requirement.PermissionType == "fls"
            ? $"{requirement.ObjectApiName}.{requirement.FieldApiName}"
            : requirement.TargetMetadataName;

        if (requirement.PermissionType == "object")
        {
            key = requirement.ObjectApiName;
        }

        if (info.OuterTag == "fieldPermissions")
        {
            var editable = requirement.PermissionValue?.ToLowerInvariant() == "true";
            return $"<{info.OuterTag}><editable>{editable.ToString().ToLowerInvariant()}</editable><field>{key}</field><readable>true</readable></{info.OuterTag}>";
        }

        if (info.OuterTag == "objectPermissions")
        {
            var perms = (requirement.PermissionValue ?? "Read").Split(',');
            return $"<{info.OuterTag}>"
                   + $"<allowCreate>{perms.Contains("Create").ToString().ToLowerInvariant()}</allowCreate>"
                   + $"<allowDelete>{perms.Contains("Delete").ToString().ToLowerInvariant()}</allowDelete>"
                   + $"<allowEdit>{perms.Contains("Edit").ToString().ToLowerInvariant()}</allowEdit>"
                   + $"<allowRead>{perms.Contains("Read").ToString().ToLowerInvariant()}</allowRead>"
                   + $"<modifyAllRecords>{perms.Contains("ModifyAll").ToString().ToLowerInvariant()}</modifyAllRecords>"
                   + $"<object>{key}</object>"
                   + $"<viewAllRecords>{perms.Contains("ViewAll").ToString().ToLowerInvariant()}</viewAllRecords>"
                   + $"</{info.OuterTag}>";
        }

        return $"<{info.OuterTag}><{info.KeyTag}>{key}</{info.KeyTag}><{info.ValueTag}>{requirement.PermissionValue}</{info.ValueTag}></{info.OuterTag}>";
    }

    private static string MergeBlock(string content, string newBlock, TagInfo info)
    {
        var pattern = $@"<{info.OuterTag}>.*?</{info.OuterTag}>";
        var matches = Regex.Matches(content, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var newKey = ExtractTagValue(newBlock, info.KeyTag) ?? string.Empty;

        foreach (Match match in matches)
        {
            var existingKey = ExtractTagValue(match.Value, info.KeyTag);
            if (newKey.Equals(existingKey, StringComparison.OrdinalIgnoreCase))
            {
                return content[..match.Index] + newBlock + content[(match.Index + match.Length)..];
            }
        }

        foreach (Match match in matches)
        {
            var existingKey = ExtractTagValue(match.Value, info.KeyTag);
            if (string.Compare(newKey, existingKey, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return content.Insert(match.Index, newBlock + "\n");
            }
        }

        if (matches.Count > 0)
        {
            return content.Insert(matches[^1].Index + matches[^1].Length, "\n" + newBlock);
        }

        var rootTag = content.Contains("</PermissionSet>") ? "</PermissionSet>" : "</Profile>";
        var index = content.LastIndexOf(rootTag, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? content + "\n" + newBlock : content.Insert(index, newBlock + "\n");
    }

    private static string? ExtractTagValue(string block, string tagName)
    {
        var match = Regex.Match(block, $@"<{tagName}>(.*?)</{tagName}>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static SalesforceConfigRequirement CloneRequirement(SalesforceConfigRequirement requirement, string permissionValue, string targetMetadataName)
    {
        return new SalesforceConfigRequirement
        {
            Id = requirement.Id,
            Type = requirement.Type,
            Service = requirement.Service,
            Operation = requirement.Operation,
            ObjectApiName = requirement.ObjectApiName,
            FieldApiName = requirement.FieldApiName,
            FieldType = requirement.FieldType,
            Precision = requirement.Precision,
            Scale = requirement.Scale,
            VisibleLines = requirement.VisibleLines,
            Label = requirement.Label,
            Length = requirement.Length,
            Required = requirement.Required,
            DefaultValue = requirement.DefaultValue,
            InlineHelpText = requirement.InlineHelpText,
            Description = requirement.Description,
            FieldDescription = requirement.FieldDescription,
            Formula = requirement.Formula,
            FormulaReturnType = requirement.FormulaReturnType,
            ExistingFieldApiName = requirement.ExistingFieldApiName,
            TargetMetadataName = targetMetadataName,
            TargetSectionLabel = requirement.TargetSectionLabel,
            ReplaceFieldApiName = requirement.ReplaceFieldApiName,
            VisibilityConditionSummary = requirement.VisibilityConditionSummary,
            PreferredTargetType = requirement.PreferredTargetType,
            TargetRegionOrComponent = requirement.TargetRegionOrComponent,
            TargetLayoutOrPageLabel = requirement.TargetLayoutOrPageLabel,
            ValidationRuleName = requirement.ValidationRuleName,
            ErrorMessage = requirement.ErrorMessage,
            ErrorLocation = requirement.ErrorLocation,
            PermissionType = requirement.PermissionType,
            PermissionValue = permissionValue,
            ValueSetSource = requirement.ValueSetSource,
            GlobalValueSetName = requirement.GlobalValueSetName,
            ControllingFieldApiName = requirement.ControllingFieldApiName,
            RecordTypeNames = new List<string>(requirement.RecordTypeNames),
            RelationshipTargetObject = requirement.RelationshipTargetObject,
            RelationshipType = requirement.RelationshipType,
            RelationshipLabel = requirement.RelationshipLabel,
            RelationshipName = requirement.RelationshipName,
            AudienceName = requirement.AudienceName,
            NeedsUserConfirmation = requirement.NeedsUserConfirmation,
            IsResolved = requirement.IsResolved,
            AmbiguityReason = requirement.AmbiguityReason,
            ResolutionOptions = requirement.ResolutionOptions.Select(option => new ResolutionOption
            {
                Id = option.Id,
                Label = option.Label,
                Type = option.Type,
                Description = option.Description
            }).ToList(),
            PicklistValues = new List<string>(requirement.PicklistValues),
            PicklistEntries = requirement.PicklistEntries.Select(item => new PicklistValueRequirement
            {
                ApiValue = item.ApiValue,
                Label = item.Label,
                Default = item.Default,
                ControllingValues = new List<string>(item.ControllingValues)
            }).ToList(),
            PicklistRenames = requirement.PicklistRenames.Select(item => new PicklistValueRenameRequirement
            {
                CurrentApiValue = item.CurrentApiValue,
                CurrentLabel = item.CurrentLabel,
                NewLabel = item.NewLabel
            }).ToList(),
            KeepPicklistValuesInOrder = requirement.KeepPicklistValuesInOrder,
            AddGlobalValueSetValuesToAllRecordTypes = requirement.AddGlobalValueSetValuesToAllRecordTypes,
            PermissionSetNames = new List<string>(requirement.PermissionSetNames),
            CustomMetadataTypeApiName = requirement.CustomMetadataTypeApiName,
            RecordDeveloperName = requirement.RecordDeveloperName,
            CustomMetadataValues = new Dictionary<string, string>(requirement.CustomMetadataValues, StringComparer.OrdinalIgnoreCase),
            ProfileAccess = requirement.ProfileAccess,
            SuggestedFiles = new List<string>(requirement.SuggestedFiles),
            SuggestedTriggerEvent = requirement.SuggestedTriggerEvent,
            SuggestedHelperMethodName = requirement.SuggestedHelperMethodName,
            ImplementationStrategy = requirement.ImplementationStrategy,
            ImplementationKind = requirement.ImplementationKind,
            EventInvocation = requirement.EventInvocation,
            HelperMethodCode = requirement.HelperMethodCode,
            TestMethodName = requirement.TestMethodName,
            TestMethodCode = requirement.TestMethodCode,
            RequiresSecondAiPass = requirement.RequiresSecondAiPass
        };
    }

    private static string EscapeXml(string value)
    {
        return System.Security.SecurityElement.Escape(value) ?? string.Empty;
    }

    private static string BuildFallbackLabelFromFileName(string path, string suffix)
    {
        var fileName = Path.GetFileName(path);
        return fileName.Replace(suffix, string.Empty, StringComparison.OrdinalIgnoreCase).Replace("_", " ");
    }
}
