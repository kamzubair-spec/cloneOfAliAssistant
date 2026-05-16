using System.Text;
using eZBERP_AI_IDE.Models;
using File = System.IO.File;

namespace eZBERP_AI_IDE.Services;

public sealed class RecordTypeManagementService : IRepositoryAwareConfigWorkItemHandler
{
    public string ServiceName => nameof(RecordTypeManagementService);

    public bool CanHandle(SalesforceConfigRequirement requirement)
    {
        return requirement.Type.Equals("record_type", StringComparison.OrdinalIgnoreCase)
               && !requirement.Operation.Equals("create", StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(requirement.ObjectApiName)
               && !string.IsNullOrWhiteSpace(requirement.TargetMetadataName)
               && !string.IsNullOrWhiteSpace(requirement.FieldApiName)
               && (requirement.PicklistEntries.Count > 0 || requirement.PicklistValues.Count > 0);
    }

    public bool CanHandle(string repoPath, SalesforceConfigRequirement requirement)
    {
        return CanHandle(requirement) && File.Exists(ResolvePath(repoPath, requirement));
    }

    public string BuildCannotHandleReason(string repoPath, SalesforceConfigRequirement requirement)
    {
        if (requirement.Operation.Equals("create", StringComparison.OrdinalIgnoreCase))
        {
            return "Creating new record types is outside the current scope. Only existing record type picklist values can be updated.";
        }

        if (!CanHandle(requirement))
        {
            return "Record type updates currently need objectApiName, targetMetadataName, fieldApiName, and picklist values.";
        }

        return File.Exists(ResolvePath(repoPath, requirement))
            ? "Record type requirement is supported."
            : $"Record type metadata was not found: {requirement.ObjectApiName}.{requirement.TargetMetadataName}";
    }

    public async Task<FileChangeSet?> BuildChangeSetAsync(string repoPath, SalesforceConfigRequirement requirement)
    {
        var path = ResolvePath(repoPath, requirement);
        var existing = await File.ReadAllTextAsync(path);
        var proposed = UpsertPicklistValues(existing, requirement);
        return new FileChangeSet(
            $"Record type metadata change for {requirement.ObjectApiName}.{requirement.TargetMetadataName}",
            new[] { new FileChangeProposal(Path.GetRelativePath(repoPath, path), existing, proposed, true) });
    }

    private static string UpsertPicklistValues(string existing, SalesforceConfigRequirement requirement)
    {
        var fieldTag = $"<picklist>{requirement.FieldApiName}</picklist>";
        var sectionStart = existing.IndexOf(fieldTag, StringComparison.OrdinalIgnoreCase);
        if (sectionStart < 0)
        {
            var closing = existing.IndexOf("</RecordType>", StringComparison.OrdinalIgnoreCase);
            if (closing < 0)
            {
                throw new InvalidOperationException("Record type XML does not contain a closing </RecordType> tag.");
            }

            var builder = new StringBuilder();
            builder.AppendLine("    <picklistValues>");
            builder.AppendLine($"        <picklist>{requirement.FieldApiName}</picklist>");
            foreach (var entry in BuildEntries(requirement))
            {
                builder.AppendLine($"        <values><fullName>{EscapeXml(entry.ApiValue)}</fullName><default>{entry.Default.ToString().ToLowerInvariant()}</default></values>");
            }
            builder.AppendLine("    </picklistValues>");
            return existing.Insert(closing, builder.ToString());
        }

        var sectionEnd = existing.IndexOf("</picklistValues>", sectionStart, StringComparison.OrdinalIgnoreCase);
        if (sectionEnd < 0)
        {
            throw new InvalidOperationException("Could not locate the target record type picklistValues section.");
        }

        sectionEnd += "</picklistValues>".Length;
        var section = existing[sectionStart..sectionEnd];
        var insertIndex = existing.LastIndexOf("</picklistValues>", sectionEnd, StringComparison.OrdinalIgnoreCase);
        var additions = new StringBuilder();
        foreach (var entry in BuildEntries(requirement))
        {
            if (section.Contains($"<fullName>{entry.ApiValue}</fullName>", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            additions.AppendLine($"        <values><fullName>{EscapeXml(entry.ApiValue)}</fullName><default>{entry.Default.ToString().ToLowerInvariant()}</default></values>");
        }

        return additions.Length == 0 ? existing : existing.Insert(insertIndex, additions.ToString());
    }

    private static List<PicklistValueRequirement> BuildEntries(SalesforceConfigRequirement requirement)
    {
        var entries = requirement.PicklistEntries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.ApiValue) || !string.IsNullOrWhiteSpace(entry.Label))
            .Select(entry => new PicklistValueRequirement
            {
                ApiValue = string.IsNullOrWhiteSpace(entry.ApiValue) ? entry.Label.Trim() : entry.ApiValue.Trim(),
                Label = entry.Label,
                Default = entry.Default
            })
            .ToList();

        entries.AddRange(requirement.PicklistValues
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => new PicklistValueRequirement { ApiValue = value.Trim(), Label = value.Trim(), Default = false }));

        return entries;
    }

    private static string ResolvePath(string repoPath, SalesforceConfigRequirement requirement)
        => Path.Combine(repoPath, "force-app", "main", "default", "objects", requirement.ObjectApiName, "recordTypes", $"{requirement.TargetMetadataName}.recordType-meta.xml");

    private static string EscapeXml(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
}
