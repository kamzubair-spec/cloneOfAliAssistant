using System.Text.RegularExpressions;
using eZBERP_AI_IDE.Models;
using File = System.IO.File;

namespace eZBERP_AI_IDE.Services;

public sealed class PermissionManagementService : IRepositoryAwareConfigWorkItemHandler
{
    public string ServiceName => nameof(PermissionManagementService);

    public bool CanHandle(SalesforceConfigRequirement requirement)
    {
        return requirement.Type.Equals("profile_metadata", StringComparison.OrdinalIgnoreCase)
               || requirement.Type.Equals("permission_set", StringComparison.OrdinalIgnoreCase)
               || requirement.Type.Equals("custom_permission", StringComparison.OrdinalIgnoreCase)
               || requirement.Type.Equals("profile_fls_update", StringComparison.OrdinalIgnoreCase)
               || requirement.Type.Equals("permission_set_fls_update", StringComparison.OrdinalIgnoreCase);
    }

    public bool CanHandle(string repoPath, SalesforceConfigRequirement requirement)
    {
        return CanHandle(requirement);
    }

    public string BuildCannotHandleReason(string repoPath, SalesforceConfigRequirement requirement)
    {
        return "Only Profile, Permission Set, and Custom Permission requirements are supported.";
    }

    public async Task<FileChangeSet?> BuildChangeSetAsync(string repoPath, SalesforceConfigRequirement requirement)
    {
        if (requirement.Type.Equals("custom_permission", StringComparison.OrdinalIgnoreCase))
        {
            return await BuildCustomPermissionChangeSetAsync(repoPath, requirement);
        }

        var proposals = new List<FileChangeProposal>();
        var targetPaths = await ResolveTargetPathsAsync(repoPath, requirement);

        foreach (var path in targetPaths)
        {
            var existingContent = File.Exists(path) ? await File.ReadAllTextAsync(path) : BuildDefaultXml(requirement);
            var proposedContent = await Task.Run(() => ProcessSurgicalEdit(existingContent, requirement));
            
            proposals.Add(new FileChangeProposal(
                Path.GetRelativePath(repoPath, path),
                existingContent,
                proposedContent,
                File.Exists(path)));
        }

        return new FileChangeSet($"Permission updates for {requirement.Id}", proposals);
    }

    private string BuildDefaultXml(SalesforceConfigRequirement requirement)
    {
        if (requirement.Type.Contains("permission_set", StringComparison.OrdinalIgnoreCase))
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<PermissionSet xmlns=\"http://soap.sforce.com/2006/04/metadata\">\n    <label>" + (requirement.Label ?? requirement.TargetMetadataName) + "</label>\n</PermissionSet>";
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
            
            foreach (var name in requirement.PermissionSetNames)
            {
                var fileName = NormalizeFileName(name) + ".permissionset-meta.xml";
                var fullPath = Path.Combine(dir, fileName);
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

            if (requirement.ProfileAccess?.ApplyReadOnlyToRemainingProfiles == true)
            {
                paths.AddRange(Directory.GetFiles(dir, "*.profile-meta.xml"));
            }
            else
            {
                foreach (var name in profileNames)
                {
                    var fileName = NormalizeFileName(name) + ".profile-meta.xml";
                    var fullPath = Path.Combine(dir, fileName);
                    paths.Add(fullPath);
                }
            }
        }

        return paths.Distinct().ToList();
    }

    private string NormalizeFileName(string name) => name.Replace(" ", "_");

    private async Task<FileChangeSet?> BuildCustomPermissionChangeSetAsync(string repoPath, SalesforceConfigRequirement requirement)
    {
        var name = requirement.TargetMetadataName ?? requirement.Label;
        var relativePath = Path.Combine("force-app", "main", "default", "customPermissions", $"{name}.customPermission-meta.xml");
        var path = Path.Combine(repoPath, relativePath);
        
        var existing = File.Exists(path) ? await File.ReadAllTextAsync(path) : string.Empty;
        var proposed = $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <CustomPermission xmlns="http://soap.sforce.com/2006/04/metadata">
            <isLicensed>false</isLicensed>
            <label>{requirement.Label ?? name}</label>
            <description>{requirement.Description}</description>
        </CustomPermission>
        """;

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

    private record TagInfo(string OuterTag, string KeyTag, string ValueTag, string RootTag);

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
            var editable = requirement.PermissionValue?.ToLowerInvariant() == "true" || (requirement.ProfileAccess?.EditableProfiles.Count > 0);
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
}
