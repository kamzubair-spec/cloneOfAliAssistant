using System.Text.RegularExpressions;
using eZBERP_AI_IDE.Models;
using File = System.IO.File;

namespace eZBERP_AI_IDE.Services;

public sealed class PermissionManagementService : IRepositoryAwareConfigWorkItemHandler
{
    public string ServiceName => nameof(PermissionManagementService);

    public bool CanHandle(SalesforceConfigRequirement requirement)
    {
        return PermissionToolingCatalog.IsSupportedRequirementType(requirement.Type)
               && PermissionToolingCatalog.IsSupportedPermissionType(requirement.PermissionType);
    }

    public bool CanHandle(string repoPath, SalesforceConfigRequirement requirement)
    {
        if (!CanHandle(requirement))
        {
            return false;
        }

        var baseDir = Path.Combine(repoPath, "force-app", "main", "default");
        return Directory.Exists(baseDir);
    }

    public string BuildCannotHandleReason(string repoPath, SalesforceConfigRequirement requirement)
    {
        return PermissionToolingCatalog.UnsupportedRequirementMessage;
    }

    public async Task<FileChangeSet?> BuildChangeSetAsync(string repoPath, SalesforceConfigRequirement requirement)
    {
        if (!CanHandle(repoPath, requirement))
        {
            return null;
        }

        if (requirement.Type.Equals("custom_permission", StringComparison.OrdinalIgnoreCase))
        {
            return await BuildCustomPermissionChangeSetAsync(repoPath, requirement);
        }

        var proposals = new List<FileChangeProposal>();
        var targetPaths = await ResolveTargetPathsAsync(repoPath, requirement);

        foreach (var path in targetPaths)
        {
            var scopedRequirement = ScopeRequirementForTarget(path, requirement);
            var existingContent = File.Exists(path) ? await File.ReadAllTextAsync(path) : BuildDefaultXml(scopedRequirement, path);
            var proposedContent = await Task.Run(() => ProcessSurgicalEdit(existingContent, scopedRequirement));
            
            proposals.Add(new FileChangeProposal(
                Path.GetRelativePath(repoPath, path),
                existingContent,
                proposedContent,
                File.Exists(path)));
        }

        return new FileChangeSet($"Permission updates for {requirement.Id}", proposals);
    }

    private string BuildDefaultXml(SalesforceConfigRequirement requirement, string path)
    {
        if (requirement.Type.Contains("permission_set", StringComparison.OrdinalIgnoreCase))
        {
            var label = string.IsNullOrWhiteSpace(requirement.Label)
                ? BuildFallbackLabelFromFileName(path, ".permissionset-meta.xml")
                : requirement.Label;
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<PermissionSet xmlns=\"http://soap.sforce.com/2006/04/metadata\">\n<label>" + label + "</label>\n</PermissionSet>";
        }
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Profile xmlns=\"http://soap.sforce.com/2006/04/metadata\">\n</Profile>";
    }

    private async Task<List<string>> ResolveTargetPathsAsync(string repoPath, SalesforceConfigRequirement requirement)
    {
        var paths = new List<string>();
        var baseDir = Path.Combine(repoPath, "force-app", "main", "default");

        if (requirement.Type.Contains("permission_set", StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.Combine(baseDir, "permissionsets");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var names = requirement.PermissionSetNames
                .Concat(string.IsNullOrWhiteSpace(requirement.TargetMetadataName) ? Enumerable.Empty<string>() : new[] { requirement.TargetMetadataName })
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var name in names)
            {
                var fullPath = ResolveMetadataPath(dir, name, ".permissionset-meta.xml", preferUnderscoreVariant: true);
                paths.Add(fullPath);
            }
        }
        else
        {
            var dir = Path.Combine(baseDir, "profiles");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var profileNames = requirement.ProfileAccess?.EditableProfiles
                .Concat(requirement.ProfileAccess?.ReadOnlyProfiles ?? Enumerable.Empty<string>())
                .ToList() ?? new List<string>();

            if (!string.IsNullOrWhiteSpace(requirement.TargetMetadataName))
            {
                profileNames.Add(requirement.TargetMetadataName);
            }

            if (requirement.ProfileAccess?.ApplyReadOnlyToRemainingProfiles == true)
            {
                paths.AddRange(Directory.GetFiles(dir, "*.profile-meta.xml"));
            }
            else
            {
                foreach (var name in profileNames)
                {
                    var fullPath = ResolveMetadataPath(dir, name, ".profile-meta.xml", preferUnderscoreVariant: false);
                    paths.Add(fullPath);
                }
            }
        }

        return paths.Distinct().ToList();
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
            if (!exact.Equals(underscore, StringComparison.OrdinalIgnoreCase)) yield return exact;
            if (!space.Equals(underscore, StringComparison.OrdinalIgnoreCase) && !space.Equals(exact, StringComparison.OrdinalIgnoreCase)) yield return space;
            yield break;
        }

        yield return exact;
        if (!space.Equals(exact, StringComparison.OrdinalIgnoreCase)) yield return space;
        if (!underscore.Equals(exact, StringComparison.OrdinalIgnoreCase) && !underscore.Equals(space, StringComparison.OrdinalIgnoreCase)) yield return underscore;
    }

    private async Task<FileChangeSet?> BuildCustomPermissionChangeSetAsync(string repoPath, SalesforceConfigRequirement requirement)
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

    private string ProcessSurgicalEdit(string content, SalesforceConfigRequirement requirement)
    {
        var tagInfo = GetTagInfo(requirement);
        if (tagInfo == null) return content;

        var newBlock = BuildBlock(tagInfo, requirement);
        return MergeBlock(content, newBlock, tagInfo, requirement);
    }

    private record TagInfo(string OuterTag, string KeyTag, string? ValueTag, string RootTag);

    private TagInfo? GetTagInfo(SalesforceConfigRequirement requirement)
    {
        var type = requirement.PermissionType?.ToLowerInvariant() ?? "";
        if (requirement.Type.Equals("profile_fls_update", StringComparison.OrdinalIgnoreCase) || requirement.Type.Equals("permission_set_fls_update", StringComparison.OrdinalIgnoreCase)) type = "fls";

        return type switch
        {
            "fls" => new TagInfo("fieldPermissions", "field", null, ""), // FLS has multiple value tags
            "tab" => new TagInfo("tabVisibilities", "tab", "visibility", ""),
            "apex_class" => new TagInfo("classAccesses", "apexClass", "enabled", ""),
            "object" => new TagInfo("objectPermissions", "object", null, ""), // Multiple value tags
            "custom_permission" => new TagInfo("customPermissions", "name", "enabled", ""),
            "apex_page" => new TagInfo("pageAccesses", "apexPage", "enabled", ""),
            "record_type" => new TagInfo("recordTypeVisibilities", "recordType", "visible", ""),
            "application" => new TagInfo("applicationVisibilities", "application", "visible", ""),
            "user_permission" => new TagInfo("userPermissions", "name", "enabled", ""),
            _ => null
        };
    }

    private string BuildBlock(TagInfo info, SalesforceConfigRequirement requirement)
    {
        var key = requirement.PermissionType == "fls" ? $"{requirement.ObjectApiName}.{requirement.FieldApiName}" : requirement.TargetMetadataName;
        if (requirement.PermissionType == "object") key = requirement.ObjectApiName;

        if (info.OuterTag == "fieldPermissions")
        {
            var editable = requirement.PermissionValue?.ToLowerInvariant() == "true";
            return $"<{info.OuterTag}><editable>{editable.ToString().ToLowerInvariant()}</editable><field>{key}</field><readable>true</readable></{info.OuterTag}>";
        }

        if (info.OuterTag == "objectPermissions")
        {
            var perms = (requirement.PermissionValue ?? "Read").Split(',');
            return $"<{info.OuterTag}>" +
                   $"<allowCreate>{perms.Contains("Create").ToString().ToLowerInvariant()}</allowCreate>" +
                   $"<allowDelete>{perms.Contains("Delete").ToString().ToLowerInvariant()}</allowDelete>" +
                   $"<allowEdit>{perms.Contains("Edit").ToString().ToLowerInvariant()}</allowEdit>" +
                   $"<allowRead>{perms.Contains("Read").ToString().ToLowerInvariant()}</allowRead>" +
                   $"<modifyAllRecords>{perms.Contains("ModifyAll").ToString().ToLowerInvariant()}</modifyAllRecords>" +
                   $"<object>{key}</object>" +
                   $"<viewAllRecords>{perms.Contains("ViewAll").ToString().ToLowerInvariant()}</viewAllRecords>" +
                   $"</{info.OuterTag}>";
        }

        return $"<{info.OuterTag}><{info.KeyTag}>{key}</{info.KeyTag}><{info.ValueTag}>{requirement.PermissionValue}</{info.ValueTag}></{info.OuterTag}>";
    }

    private string MergeBlock(string content, string newBlock, TagInfo info, SalesforceConfigRequirement requirement)
    {
        var pattern = $@"<{info.OuterTag}>.*?</{info.OuterTag}>";
        var matches = Regex.Matches(content, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var newKey = ExtractTagValue(newBlock, info.KeyTag) ?? "";

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
        var index = content.LastIndexOf(rootTag);
        return index < 0 ? content + "\n" + newBlock : content.Insert(index, newBlock + "\n");
    }

    private string? ExtractTagValue(string block, string tagName)
    {
        var match = Regex.Match(block, $@"<{tagName}>(.*?)</{tagName}>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private SalesforceConfigRequirement ScopeRequirementForTarget(string path, SalesforceConfigRequirement requirement)
    {
        if (!requirement.Type.StartsWith("profile", StringComparison.OrdinalIgnoreCase))
        {
            return requirement;
        }

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

    private SalesforceConfigRequirement CloneRequirement(SalesforceConfigRequirement requirement, string permissionValue, string targetMetadataName)
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

    private string BuildCustomPermissionContent(string existing, SalesforceConfigRequirement requirement, string name)
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

    private string ReplaceOrInsertSimpleTag(string content, string tagName, string value)
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
