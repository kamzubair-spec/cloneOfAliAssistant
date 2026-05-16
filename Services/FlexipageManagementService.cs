using eZBERP_AI_IDE.Models;
using File = System.IO.File;

namespace eZBERP_AI_IDE.Services;

public sealed class FlexipageManagementService : IRepositoryAwareConfigWorkItemHandler
{
    private readonly LayoutFlexipageResolutionService _resolver = new();

    public string ServiceName => nameof(FlexipageManagementService);

    public bool CanHandle(SalesforceConfigRequirement requirement)
    {
        return requirement.Type.Equals("flexipage", StringComparison.OrdinalIgnoreCase)
               && !requirement.Operation.Equals("create", StringComparison.OrdinalIgnoreCase)
               && (HasFieldReplacement(requirement) || IsVisibilityRemoval(requirement));
    }

    public bool CanHandle(string repoPath, SalesforceConfigRequirement requirement)
    {
        if (!CanHandle(requirement))
        {
            return false;
        }

        var resolution = _resolver.ResolveFlexipage(repoPath, requirement);
        if (!resolution.IsSupported)
        {
            return false;
        }

        var existing = File.ReadAllText(resolution.FilePath);
        if (HasFieldReplacement(requirement))
        {
            var oldField = FirstNonBlank(requirement.ReplaceFieldApiName, requirement.ExistingFieldApiName);
            return existing.Contains(oldField, StringComparison.OrdinalIgnoreCase);
        }

        return IsVisibilityRemoval(requirement)
               && ExtractFieldNames(requirement).Any(field => HasVisibilityRuleForField(existing, field));
    }

    public string BuildCannotHandleReason(string repoPath, SalesforceConfigRequirement requirement)
    {
        if (requirement.Operation.Equals("create", StringComparison.OrdinalIgnoreCase))
        {
            return "Creating new flexipages is outside the current scope. Only existing flexipage files can be updated.";
        }

        if (!HasFieldReplacement(requirement) && !IsVisibilityRemoval(requirement))
        {
            return "Flexipage V1 supports replacing an existing field reference or removing field visibility criteria from a named existing flexipage.";
        }

        var resolution = _resolver.ResolveFlexipage(repoPath, requirement);
        if (!resolution.IsSupported)
        {
            return resolution.Reason;
        }

        var existing = File.ReadAllText(resolution.FilePath);
        if (HasFieldReplacement(requirement))
        {
            var oldField = FirstNonBlank(requirement.ReplaceFieldApiName, requirement.ExistingFieldApiName);
            return existing.Contains(oldField, StringComparison.OrdinalIgnoreCase)
                ? "Flexipage requirement is supported."
                : $"The existing flexipage was found, but it does not reference {oldField}.";
        }

        var fields = ExtractFieldNames(requirement);
        if (fields.Count == 0)
        {
            return "The existing flexipage was found, but this visibility requirement does not include explicit field API names. Flexipage visibility removal is only safe when the exact target fields are known.";
        }

        var matchingFields = fields.Where(field => HasVisibilityRuleForField(existing, field)).ToList();
        return matchingFields.Count > 0
            ? "Flexipage visibility requirement is supported."
            : $"The existing flexipage was found, but no visibility rule was found for the extracted field(s): {string.Join(", ", fields)}.";
    }

    public async Task<FileChangeSet?> BuildChangeSetAsync(string repoPath, SalesforceConfigRequirement requirement)
    {
        var resolution = _resolver.ResolveFlexipage(repoPath, requirement);
        if (!resolution.IsSupported)
        {
            throw new InvalidOperationException(resolution.Reason);
        }

        var existingContent = await File.ReadAllTextAsync(resolution.FilePath);
        var proposedContent = existingContent;
        if (HasFieldReplacement(requirement))
        {
            var oldField = FirstNonBlank(requirement.ReplaceFieldApiName, requirement.ExistingFieldApiName);
            proposedContent = ReplaceFieldReferences(existingContent, oldField, requirement.FieldApiName);
        }
        else if (IsVisibilityRemoval(requirement))
        {
            proposedContent = RemoveFieldVisibilityRules(existingContent, requirement);
        }

        if (proposedContent == existingContent)
        {
            var fields = ExtractFieldNames(requirement);
            var reason = fields.Count == 0
                ? "The visibility requirement did not include explicit field API names."
                : $"No visibility rule was found for the extracted field(s): {string.Join(", ", fields)}.";
            throw new InvalidOperationException($"The existing flexipage was found, but no safe matching flexipage change could be made. {reason}");
        }

        return new FileChangeSet(
            $"Flexipage metadata change for {Path.GetFileName(resolution.FilePath)}",
            new[] { new FileChangeProposal(Path.GetRelativePath(repoPath, resolution.FilePath), existingContent, proposedContent, true) });
    }

    private static string ReplaceFieldReferences(string content, string oldField, string newField)
    {
        return content
            .Replace($"Record.{oldField}", $"Record.{newField}", StringComparison.OrdinalIgnoreCase)
            .Replace($"{{!Record.{oldField}}}", $"{{!Record.{newField}}}", StringComparison.OrdinalIgnoreCase)
            .Replace(oldField, newField, StringComparison.OrdinalIgnoreCase);
    }

    private static string RemoveFieldVisibilityRules(string content, SalesforceConfigRequirement requirement)
    {
        var fields = ExtractFieldNames(requirement);
        if (fields.Count == 0)
        {
            return content;
        }

        var proposed = content;
        foreach (var field in fields)
        {
            proposed = RemoveVisibilityRuleForField(proposed, field);
        }

        return proposed;
    }

    private static string RemoveVisibilityRuleForField(string content, string fieldName)
    {
        var escapedField = System.Text.RegularExpressions.Regex.Escape(fieldName);
        return System.Text.RegularExpressions.Regex.Replace(
            content,
            $@"(?is)(<fieldInstance>\s*(?:(?!</fieldInstance>).)*?<fieldItem>Record\.{escapedField}</fieldItem>\s*(?:(?!</fieldInstance>).)*?)\s*<visibilityRule>.*?</visibilityRule>",
            "$1");
    }

    private static bool HasVisibilityRuleForField(string content, string fieldName)
    {
        var escapedField = System.Text.RegularExpressions.Regex.Escape(fieldName);
        return System.Text.RegularExpressions.Regex.IsMatch(
            content,
            $@"(?is)<fieldInstance>\s*(?:(?!</fieldInstance>).)*?<fieldItem>Record\.{escapedField}</fieldItem>\s*(?:(?!</fieldInstance>).)*?<visibilityRule>.*?</visibilityRule>");
    }

    private static List<string> ExtractFieldNames(SalesforceConfigRequirement requirement)
    {
        var candidates = string.Join(" ", new[]
        {
            requirement.FieldApiName,
            requirement.ReplaceFieldApiName,
            requirement.ExistingFieldApiName,
            requirement.Description,
            requirement.VisibilityConditionSummary
        });

        return System.Text.RegularExpressions.Regex.Matches(candidates, @"\b[A-Za-z][A-Za-z0-9_]*__c\b")
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool HasFieldReplacement(SalesforceConfigRequirement requirement)
    {
        return !string.IsNullOrWhiteSpace(FirstNonBlank(requirement.ReplaceFieldApiName, requirement.ExistingFieldApiName))
               && !string.IsNullOrWhiteSpace(requirement.FieldApiName);
    }

    private static bool IsVisibilityRemoval(SalesforceConfigRequirement requirement)
    {
        var text = $"{requirement.Operation} {requirement.Label} {requirement.Description} {requirement.VisibilityConditionSummary}";
        return text.Contains("visibility", StringComparison.OrdinalIgnoreCase)
               && (text.Contains("remove", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("always visible", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("should also always be visible", StringComparison.OrdinalIgnoreCase));
    }

    private static string FirstNonBlank(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }
}
