using System.Text.RegularExpressions;
using eZBERP_AI_IDE.Models;
using File = System.IO.File;

namespace eZBERP_AI_IDE.Services;

public sealed class CustomPermissionManagementService : IConfigWorkItemHandler
{
    public string ServiceName => nameof(CustomPermissionManagementService);

    public bool CanHandle(SalesforceConfigRequirement requirement)
    {
        return requirement.Type.Equals("custom_permission", StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(GetPermissionName(requirement));
    }

    public async Task<FileChangeSet?> BuildChangeSetAsync(string repoPath, SalesforceConfigRequirement requirement)
    {
        var name = NormalizeMetadataName(GetPermissionName(requirement));
        var relativePath = Path.Combine("force-app", "main", "default", "customPermissions", $"{name}.customPermission-meta.xml");
        var path = Path.Combine(repoPath, relativePath);
        var existing = File.Exists(path) ? await File.ReadAllTextAsync(path) : string.Empty;
        var proposed = BuildXml(name, requirement);

        return new FileChangeSet(
            $"Custom permission metadata change for {name}",
            new[] { new FileChangeProposal(relativePath, existing, proposed, File.Exists(path)) });
    }

    private static string BuildXml(string name, SalesforceConfigRequirement requirement)
    {
        var label = FirstNonBlank(requirement.Label, name.Replace("_", " "));
        var description = FirstNonBlank(requirement.Description, label);
        return $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <CustomPermission xmlns="http://soap.sforce.com/2006/04/metadata">
            <description>{EscapeXml(description)}</description>
            <isLicensed>false</isLicensed>
            <label>{EscapeXml(label)}</label>
        </CustomPermission>
        """;
    }

    private static string GetPermissionName(SalesforceConfigRequirement requirement) => FirstNonBlank(requirement.TargetMetadataName, requirement.FieldApiName, requirement.Label);
    private static string NormalizeMetadataName(string value) => Regex.Replace(value.Trim(), @"[^A-Za-z0-9_]+", "_").Trim('_');
    private static string FirstNonBlank(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    private static string EscapeXml(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
}
