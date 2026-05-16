using System.Xml.Linq;
using eZBERP_AI_IDE.Models;
using File = System.IO.File;

namespace eZBERP_AI_IDE.Services;

public sealed class LayoutManagementService : IRepositoryAwareConfigWorkItemHandler
{
    private readonly LayoutFlexipageResolutionService _resolver = new();

    public string ServiceName => nameof(LayoutManagementService);

    public bool CanHandle(SalesforceConfigRequirement requirement)
    {
        return requirement.Type.Equals("layout", StringComparison.OrdinalIgnoreCase)
               && !requirement.Operation.Equals("create", StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(requirement.ObjectApiName)
               && HasLayoutOperation(requirement);
    }

    public bool CanHandle(string repoPath, SalesforceConfigRequirement requirement)
    {
        if (!CanHandle(requirement))
        {
            return false;
        }

        var resolution = _resolver.ResolveLayout(repoPath, requirement);
        if (resolution.IsSupported)
        {
            return CanApplyLayoutChange(resolution.FilePath, requirement);
        }

        return CanFallbackToFlexipage(repoPath, requirement);
    }

    public string BuildCannotHandleReason(string repoPath, SalesforceConfigRequirement requirement)
    {
        if (requirement.Operation.Equals("create", StringComparison.OrdinalIgnoreCase))
        {
            return "Creating new layouts is outside the current scope. Only existing layout files can be updated.";
        }

        if (!HasLayoutOperation(requirement))
        {
            return "Layout updates need a target field plus either a section to add to, a field to replace, or a remove operation.";
        }

        var resolution = _resolver.ResolveLayout(repoPath, requirement);
        if (resolution.IsSupported)
        {
            return CanApplyLayoutChange(resolution.FilePath, requirement)
                ? "Layout requirement is supported."
                : BuildLayoutChangeUnsupportedReason(resolution.FilePath, requirement);
        }

        return CanFallbackToFlexipage(repoPath, requirement)
            ? "No matching layout was found, but an existing flexipage can be updated for this field-reference replacement."
            : resolution.Reason;
    }

    public async Task<FileChangeSet?> BuildChangeSetAsync(string repoPath, SalesforceConfigRequirement requirement)
    {
        var resolution = _resolver.ResolveLayout(repoPath, requirement);
        if (!resolution.IsSupported)
        {
            if (CanFallbackToFlexipage(repoPath, requirement))
            {
                return await BuildFlexipageFallbackChangeSetAsync(repoPath, requirement);
            }

            throw new InvalidOperationException(resolution.Reason);
        }

        var existingContent = await File.ReadAllTextAsync(resolution.FilePath);
        var proposedContent = ApplyLayoutChange(existingContent, requirement);

        return new FileChangeSet(
            $"Layout metadata change for {Path.GetFileName(resolution.FilePath)}",
            new[] { new FileChangeProposal(Path.GetRelativePath(repoPath, resolution.FilePath), existingContent, proposedContent, true) });
    }

    private static bool CanApplyLayoutChange(string filePath, SalesforceConfigRequirement requirement)
    {
        try
        {
            var document = XDocument.Parse(File.ReadAllText(filePath), LoadOptions.PreserveWhitespace);
            var ns = document.Root?.Name.Namespace ?? XNamespace.None;
            var fieldName = requirement.FieldApiName;
            var replaceFieldName = FirstNonBlank(requirement.ReplaceFieldApiName, requirement.ExistingFieldApiName);

            if (IsRemove(requirement))
            {
                return LayoutContainsField(document, ns, FirstNonBlank(fieldName, replaceFieldName));
            }

            if (!string.IsNullOrWhiteSpace(replaceFieldName))
            {
                return LayoutContainsField(document, ns, replaceFieldName);
            }

            return LayoutContainsField(document, ns, fieldName)
                   || FindSection(document, ns, requirement.TargetSectionLabel) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildLayoutChangeUnsupportedReason(string filePath, SalesforceConfigRequirement requirement)
    {
        var document = XDocument.Parse(File.ReadAllText(filePath), LoadOptions.PreserveWhitespace);
        var ns = document.Root?.Name.Namespace ?? XNamespace.None;
        var fieldName = requirement.FieldApiName;
        var replaceFieldName = FirstNonBlank(requirement.ReplaceFieldApiName, requirement.ExistingFieldApiName);

        if (!string.IsNullOrWhiteSpace(replaceFieldName) && !LayoutContainsField(document, ns, replaceFieldName))
        {
            return $"The field to replace was not found in the resolved layout: {replaceFieldName}";
        }

        if (!string.IsNullOrWhiteSpace(fieldName)
            && !LayoutContainsField(document, ns, fieldName)
            && FindSection(document, ns, requirement.TargetSectionLabel) is null)
        {
            return $"The requested layout section was not found: {requirement.TargetSectionLabel}";
        }

        return "The resolved layout cannot be updated safely for this requirement.";
    }

    private static bool LayoutContainsField(XDocument document, XNamespace ns, string fieldName)
    {
        return !string.IsNullOrWhiteSpace(fieldName)
               && document.Descendants(ns + "field")
                   .Any(element => element.Value.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
    }
    private bool CanFallbackToFlexipage(string repoPath, SalesforceConfigRequirement requirement)
    {
        var oldField = FirstNonBlank(requirement.ReplaceFieldApiName, requirement.ExistingFieldApiName);
        if (string.IsNullOrWhiteSpace(oldField) || string.IsNullOrWhiteSpace(requirement.FieldApiName))
        {
            return false;
        }

        var resolution = _resolver.ResolveFlexipage(repoPath, requirement);
        return resolution.IsSupported
               && File.ReadAllText(resolution.FilePath).Contains(oldField, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<FileChangeSet> BuildFlexipageFallbackChangeSetAsync(string repoPath, SalesforceConfigRequirement requirement)
    {
        var resolution = _resolver.ResolveFlexipage(repoPath, requirement);
        if (!resolution.IsSupported)
        {
            throw new InvalidOperationException(resolution.Reason);
        }

        var oldField = FirstNonBlank(requirement.ReplaceFieldApiName, requirement.ExistingFieldApiName);
        var existingContent = await File.ReadAllTextAsync(resolution.FilePath);
        var proposedContent = existingContent
            .Replace($"Record.{oldField}", $"Record.{requirement.FieldApiName}", StringComparison.OrdinalIgnoreCase)
            .Replace($"{{!Record.{oldField}}}", $"{{!Record.{requirement.FieldApiName}}}", StringComparison.OrdinalIgnoreCase)
            .Replace(oldField, requirement.FieldApiName, StringComparison.OrdinalIgnoreCase);

        return new FileChangeSet(
            $"Flexipage metadata fallback change for {Path.GetFileName(resolution.FilePath)}",
            new[] { new FileChangeProposal(Path.GetRelativePath(repoPath, resolution.FilePath), existingContent, proposedContent, true) });
    }
    private static bool HasLayoutOperation(SalesforceConfigRequirement requirement)
    {
        return !string.IsNullOrWhiteSpace(requirement.FieldApiName)
               || !string.IsNullOrWhiteSpace(requirement.ReplaceFieldApiName)
               || !string.IsNullOrWhiteSpace(requirement.ExistingFieldApiName);
    }

    private static string ApplyLayoutChange(string existingContent, SalesforceConfigRequirement requirement)
    {
        var document = XDocument.Parse(existingContent, LoadOptions.PreserveWhitespace);
        var ns = document.Root?.Name.Namespace ?? XNamespace.None;
        var fieldName = requirement.FieldApiName;
        var replaceFieldName = FirstNonBlank(requirement.ReplaceFieldApiName, requirement.ExistingFieldApiName);

        if (IsRemove(requirement))
        {
            RemoveField(document, ns, FirstNonBlank(fieldName, replaceFieldName));
        }
        else if (!string.IsNullOrWhiteSpace(replaceFieldName) && !string.IsNullOrWhiteSpace(fieldName))
        {
            ReplaceField(document, ns, replaceFieldName, fieldName);
        }
        else if (!string.IsNullOrWhiteSpace(fieldName))
        {
            AddField(document, ns, fieldName, requirement.TargetSectionLabel);
        }
        else
        {
            throw new InvalidOperationException("No supported layout operation could be determined.");
        }

        return Serialize(document, existingContent);
    }

    private static void ReplaceField(XDocument document, XNamespace ns, string existingField, string newField)
    {
        var field = document.Descendants(ns + "field")
            .FirstOrDefault(element => element.Value.Equals(existingField, StringComparison.OrdinalIgnoreCase));
        if (field is null)
        {
            throw new InvalidOperationException($"The field to replace was not found in the layout: {existingField}");
        }

        field.Value = newField;
    }

    private static void AddField(XDocument document, XNamespace ns, string fieldName, string sectionLabel)
    {
        if (document.Descendants(ns + "field").Any(element => element.Value.Equals(fieldName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var section = FindSection(document, ns, sectionLabel)
                      ?? throw new InvalidOperationException($"The requested layout section was not found: {sectionLabel}");
        var column = section.Elements(ns + "layoutColumns").FirstOrDefault()
                     ?? throw new InvalidOperationException("The target layout section has no layoutColumns node.");

        column.Add(
            new XElement(ns + "layoutItems",
                new XElement(ns + "behavior", "Edit"),
                new XElement(ns + "field", fieldName)));
    }

    private static void RemoveField(XDocument document, XNamespace ns, string fieldName)
    {
        var item = document.Descendants(ns + "layoutItems")
            .FirstOrDefault(element => element.Element(ns + "field")?.Value.Equals(fieldName, StringComparison.OrdinalIgnoreCase) == true);
        if (item is null)
        {
            throw new InvalidOperationException($"The field to remove was not found in the layout: {fieldName}");
        }

        item.Remove();
    }

    private static XElement? FindSection(XDocument document, XNamespace ns, string sectionLabel)
    {
        var sections = document.Descendants(ns + "layoutSections").ToList();
        if (string.IsNullOrWhiteSpace(sectionLabel))
        {
            return sections.FirstOrDefault();
        }

        return sections.FirstOrDefault(section =>
            section.Element(ns + "label")?.Value.Equals(sectionLabel, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static bool IsRemove(SalesforceConfigRequirement requirement)
    {
        return requirement.Operation.Equals("remove", StringComparison.OrdinalIgnoreCase)
               || requirement.Operation.Equals("delete", StringComparison.OrdinalIgnoreCase)
               || requirement.Description.Contains("remove", StringComparison.OrdinalIgnoreCase);
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
}
