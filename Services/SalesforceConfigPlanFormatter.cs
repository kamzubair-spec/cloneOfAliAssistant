using System.Text;
using eZBERP_AI_IDE.Models;

namespace eZBERP_AI_IDE.Services;

public static class SalesforceConfigPlanFormatter
{
    public static string BuildPreview(SalesforceConfigPlan plan)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Salesforce config roadmap");
        builder.AppendLine(new string('=', 80));
        builder.AppendLine();
        plan ??= new SalesforceConfigPlan();
        plan.Summary ??= string.Empty;
        plan.Requirements ??= new List<SalesforceConfigRequirement>();
        plan.Questions ??= new List<string>();

        builder.AppendLine(string.IsNullOrWhiteSpace(plan.Summary) ? "No summary was provided." : plan.Summary.Trim());

        if (plan.Questions.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Open questions / assumptions");
            foreach (var question in plan.Questions.Where(q => !string.IsNullOrWhiteSpace(q)))
            {
                builder.AppendLine($"- {question.Trim()}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Planned config work");
        if (plan.Requirements.Count == 0)
        {
            builder.AppendLine("- No actionable Salesforce config items were extracted.");
            return builder.ToString();
        }

        foreach (var requirement in plan.Requirements)
        {
            builder.AppendLine($"- [{requirement.Service}] {BuildRequirementHeadline(requirement)}");

            var detail = BuildRequirementDetail(requirement);
            if (!string.IsNullOrWhiteSpace(detail))
            {
                builder.AppendLine($"  {detail}");
            }
        }

        return builder.ToString();
    }

    public static string BuildCoveragePreview(SalesforceConfigCoverage coverage)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Salesforce config coverage");
        builder.AppendLine(new string('=', 80));
        builder.AppendLine();
        coverage ??= new SalesforceConfigCoverage();
        var results = coverage.Results ?? new List<RequirementCoverageResult>();
        var totalRequirements = results.Count;
        var supportedRequirements = results.Count(result => result.IsSupported);
        var coveragePercentage = totalRequirements == 0
            ? 0
            : (int)Math.Round((decimal)supportedRequirements / totalRequirements * 100, MidpointRounding.AwayFromZero);
        builder.AppendLine($"Coverage: {supportedRequirements} of {totalRequirements} requirements supported ({coveragePercentage}%)");
        builder.AppendLine();

        var supported = results.Where(result => result.IsSupported).ToList();
        builder.AppendLine("Supported requirements");
        if (supported.Count == 0)
        {
            builder.AppendLine("- None");
        }
        else
        {
            foreach (var result in supported)
            {
                builder.AppendLine($"- {BuildRequirementHeadline(result.Requirement)}");

                var detail = BuildRequirementDetail(result.Requirement);
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    builder.AppendLine($"  {detail}");
                }

                builder.AppendLine($"  {result.Reason}");
            }
        }

        var unsupported = results.Where(result => !result.IsSupported).ToList();
        builder.AppendLine();
        builder.AppendLine("Unsupported requirements");
        if (unsupported.Count == 0)
        {
            builder.AppendLine("- None");
        }
        else
        {
            foreach (var result in unsupported)
            {
                builder.AppendLine($"- {BuildRequirementHeadline(result.Requirement)}");

                var detail = BuildRequirementDetail(result.Requirement);
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    builder.AppendLine($"  {detail}");
                }

                builder.AppendLine($"  {result.Reason}");

                if (result.AlternativeRequirement is not null)
                {
                    builder.AppendLine("  Alternative available:");
                    builder.AppendLine($"  - {BuildRequirementHeadline(result.AlternativeRequirement)}");
                    var alternativeDetail = BuildRequirementDetail(result.AlternativeRequirement);
                    if (!string.IsNullOrWhiteSpace(alternativeDetail))
                    {
                        builder.AppendLine($"    {alternativeDetail}");
                    }

                    if (!string.IsNullOrWhiteSpace(result.AlternativeReason))
                    {
                        builder.AppendLine($"    {result.AlternativeReason}");
                    }
                }
            }
        }

        builder.AppendLine();
        builder.AppendLine("Only supported requirements and explicitly listed alternatives will be proposed if you continue.");
        return builder.ToString();
    }

    public static string BuildRequirementHeadline(SalesforceConfigRequirement requirement)
    {
        var fieldRef = BuildFieldReference(requirement);
        var objectRef = FirstNonBlank(requirement.ObjectApiName, requirement.Label, requirement.Id);
        var picklistSummary = BuildPicklistSummary(requirement);

        return requirement.Type switch
        {
            "field_create" => $"Create field {fieldRef}",
            "field_update" => string.Equals(requirement.FieldType, "Formula", StringComparison.OrdinalIgnoreCase)
                ? $"Update formula on {fieldRef}"
                : $"Update field {fieldRef}",
            "field_upsert" => $"Create or update field {fieldRef}",
            "picklist" or "picklist_value" or "picklist_value_add" => $"Add picklist value {picklistSummary} to {fieldRef}",
            "validation_rule" or "validation_rule_create" => $"Create validation rule {FirstNonBlank(requirement.ValidationRuleName, requirement.Label, requirement.Id)} on {objectRef}",
            "profile_fls_update" => $"Update field-level security for {fieldRef}",
            "permission_set" => $"Update permission set access for {fieldRef}",
            "layout" => $"Update page layout for {FirstNonBlank(requirement.TargetLayoutOrPageLabel, requirement.TargetMetadataName, fieldRef, objectRef)}",
            "flexipage" => $"Update page visibility or component behavior for {FirstNonBlank(requirement.TargetLayoutOrPageLabel, requirement.TargetMetadataName, fieldRef, objectRef)}",
            "quick_action" => $"Update quick action configuration for {FirstNonBlank(fieldRef, objectRef)}",
            "flow" => $"Update flow logic for {FirstNonBlank(fieldRef, objectRef)}",
            "record_type" => $"Update record type configuration for {objectRef}",
            "custom_metadata" => $"Update custom metadata {FirstNonBlank(requirement.Label, objectRef, requirement.Id)}",
            "custom_permission" => $"Update custom permission {FirstNonBlank(requirement.Label, requirement.Id)}",
            "custom_label" => $"Update custom label {FirstNonBlank(requirement.TargetMetadataName, requirement.Label, requirement.Id)}",
            "global_value_set" => $"Update global value set {FirstNonBlank(requirement.TargetMetadataName, requirement.Label, requirement.Id)}",
            "implementation_code" => BuildImplementationCodeHeadline(requirement),
            "external_dependency" or "unsupported_requirement" => FirstNonBlank(requirement.Label, requirement.Description, requirement.Id),
            _ => FirstNonBlank(requirement.Label, fieldRef, requirement.Description, requirement.Id)
        };
    }

    public static string BuildRequirementDetail(SalesforceConfigRequirement requirement)
    {
        return requirement.Type switch
        {
            "field_create" or "field_update" or "field_upsert" => BuildFieldDetail(requirement),
            "picklist" or "picklist_value" or "picklist_value_add" => BuildPicklistDetail(requirement),
            "validation_rule" or "validation_rule_create" => BuildValidationRuleDetail(requirement),
            "profile_fls_update" => BuildProfileFlsDetail(requirement),
            "permission_set" => BuildPermissionSetDetail(requirement),
            "layout" => BuildLayoutDetail(requirement),
            "flexipage" => BuildFlexipageDetail(requirement),
            "implementation_code" => BuildImplementationCodeDetail(requirement),
            "global_value_set" => BuildGlobalValueSetDetail(requirement),
            "quick_action" or "flow" or "record_type" or "custom_metadata" or "custom_permission" or "custom_label" or "external_dependency" or "unsupported_requirement" =>
                FirstNonBlank(requirement.Description, requirement.Label),
            _ => FirstNonBlank(requirement.Description, requirement.Label)
        };
    }

    private static string BuildImplementationCodeHeadline(SalesforceConfigRequirement requirement)
    {
        var target = BuildImplementationCodeTarget(requirement);
        var summary = FirstNonBlank(
            requirement.Label,
            requirement.ImplementationStrategy,
            requirement.Description);

        if (!string.IsNullOrWhiteSpace(summary))
        {
            return summary;
        }

        if (!string.IsNullOrWhiteSpace(target))
        {
            return $"Code change for {target}";
        }

        return "Code change requirement details missing";
    }

    private static string BuildImplementationCodeTarget(SalesforceConfigRequirement requirement)
    {
        if (!string.IsNullOrWhiteSpace(requirement.ObjectApiName)
            && !string.IsNullOrWhiteSpace(requirement.FieldApiName))
        {
            return $"{requirement.ObjectApiName}.{requirement.FieldApiName}";
        }

        return FirstNonBlank(requirement.ObjectApiName, requirement.FieldApiName);
    }

    private static string BuildImplementationCodeDetail(SalesforceConfigRequirement requirement)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(requirement.Id))
        {
            parts.Add($"Requirement id: {requirement.Id}");
        }

        if (!string.IsNullOrWhiteSpace(requirement.Description))
        {
            parts.Add(requirement.Description);
        }

        if (!string.IsNullOrWhiteSpace(requirement.ImplementationKind))
        {
            parts.Add($"Implementation kind: {requirement.ImplementationKind}");
        }

        if (requirement.SuggestedFiles.Count > 0)
        {
            parts.Add($"Suggested files: {string.Join(", ", requirement.SuggestedFiles)}");
        }

        if (!string.IsNullOrWhiteSpace(requirement.SuggestedTriggerEvent))
        {
            parts.Add($"Trigger event: {requirement.SuggestedTriggerEvent}");
        }

        if (parts.Count == 1 && parts[0].StartsWith("Requirement id:", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add("The analyzer did not provide a readable code-change description.");
        }

        return string.Join(". ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string BuildFieldReference(SalesforceConfigRequirement requirement)
    {
        return string.IsNullOrWhiteSpace(requirement.FieldApiName)
            ? FirstNonBlank(requirement.ObjectApiName, requirement.Label, requirement.Id)
            : $"{requirement.ObjectApiName}.{requirement.FieldApiName}";
    }

    private static string BuildFieldDetail(SalesforceConfigRequirement requirement)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(requirement.FieldType))
        {
            parts.Add($"Type: {requirement.FieldType}");
        }

        if (!string.IsNullOrWhiteSpace(requirement.Label))
        {
            parts.Add($"Label: {requirement.Label}");
        }

        if (requirement.Length.HasValue)
        {
            parts.Add($"Length: {requirement.Length.Value}");
        }

        if (!string.IsNullOrWhiteSpace(requirement.InlineHelpText))
        {
            parts.Add($"Help text: {requirement.InlineHelpText}");
        }

        if (!string.IsNullOrWhiteSpace(requirement.DefaultValue))
        {
            parts.Add($"Default: {requirement.DefaultValue}");
        }

        if (!string.IsNullOrWhiteSpace(requirement.Formula) && string.Equals(requirement.FieldType, "Formula", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"Formula return type: {FirstNonBlank(requirement.FormulaReturnType, "unspecified")}");
        }

        if (!string.IsNullOrWhiteSpace(requirement.Description))
        {
            parts.Add(requirement.Description);
        }

        return string.Join(". ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string BuildPicklistSummary(SalesforceConfigRequirement requirement)
    {
        var values = requirement.PicklistEntries
            .Select(entry => string.IsNullOrWhiteSpace(entry.ApiValue) || string.Equals(entry.ApiValue, entry.Label, StringComparison.OrdinalIgnoreCase)
                ? entry.Label
                : $"{entry.Label} [{entry.ApiValue}]")
            .Concat(requirement.PicklistValues)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        return values.Count == 0
            ? FirstNonBlank(requirement.Label, requirement.Description, requirement.Id)
            : string.Join(", ", values);
    }

    private static string BuildPicklistDetail(SalesforceConfigRequirement requirement)
    {
        var parts = new List<string>();

        if (requirement.PicklistEntries.Count > 0)
        {
            var entries = requirement.PicklistEntries.Select(entry =>
                entry.Default
                    ? $"{entry.Label} [{entry.ApiValue}] (default)"
                    : string.IsNullOrWhiteSpace(entry.ApiValue) || string.Equals(entry.ApiValue, entry.Label, StringComparison.OrdinalIgnoreCase)
                        ? entry.Label
                        : $"{entry.Label} [{entry.ApiValue}]");
            parts.Add($"Requested values: {string.Join(", ", entries)}");
            var controllingValues = requirement.PicklistEntries
                .SelectMany(entry => entry.ControllingValues)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (controllingValues.Count > 0)
            {
                parts.Add($"Controlling values: {string.Join(", ", controllingValues)}");
            }

            if (requirement.KeepPicklistValuesInOrder)
            {
                parts.Add("Keep picklist values in order");
            }
        }
        else if (requirement.PicklistValues.Count > 0)
        {
            parts.Add($"Requested values: {string.Join(", ", requirement.PicklistValues)}");
        }

        if (!string.IsNullOrWhiteSpace(requirement.Description))
        {
            parts.Add(requirement.Description);
        }

        return string.Join(". ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string BuildGlobalValueSetDetail(SalesforceConfigRequirement requirement)
    {
        var parts = new List<string>();

        if (requirement.PicklistRenames.Count > 0)
        {
            var renames = requirement.PicklistRenames
                .Where(rename => !string.IsNullOrWhiteSpace(rename.NewLabel))
                .Select(rename => $"{FirstNonBlank(rename.CurrentLabel, rename.CurrentApiValue)} -> {rename.NewLabel}");
            parts.Add($"Label renames: {string.Join(", ", renames)}");
        }

        if (requirement.PicklistEntries.Count > 0 || requirement.PicklistValues.Count > 0)
        {
            parts.Add(BuildPicklistDetail(requirement));
            parts.Add("New global values are inserted in existing value order");
        }

        if (requirement.AddGlobalValueSetValuesToAllRecordTypes)
        {
            parts.Add("Add new values to all record types for fields using this global value set");
        }

        if (!string.IsNullOrWhiteSpace(requirement.Description))
        {
            parts.Add(requirement.Description);
        }

        return string.Join(". ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string BuildValidationRuleDetail(SalesforceConfigRequirement requirement)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(requirement.FieldApiName))
        {
            parts.Add($"Field: {requirement.ObjectApiName}.{requirement.FieldApiName}");
        }

        if (!string.IsNullOrWhiteSpace(requirement.ErrorMessage))
        {
            parts.Add($"Error message: {requirement.ErrorMessage}");
        }

        if (!string.IsNullOrWhiteSpace(requirement.ErrorLocation))
        {
            parts.Add($"Error location: {requirement.ErrorLocation}");
        }

        if (!string.IsNullOrWhiteSpace(requirement.Formula))
        {
            parts.Add($"Formula: {requirement.Formula}");
        }

        return string.Join(". ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string BuildProfileFlsDetail(SalesforceConfigRequirement requirement)
    {
        if (requirement.ProfileAccess is null)
        {
            return FirstNonBlank(requirement.Description, requirement.Label);
        }

        var parts = new List<string>();
        if (requirement.ProfileAccess.EditableProfiles.Count > 0)
        {
            parts.Add($"Read/write: {string.Join(", ", requirement.ProfileAccess.EditableProfiles)}");
        }

        if (requirement.ProfileAccess.ReadOnlyProfiles.Count > 0)
        {
            parts.Add($"Read-only: {string.Join(", ", requirement.ProfileAccess.ReadOnlyProfiles)}");
        }

        if (requirement.ProfileAccess.ApplyReadOnlyToRemainingProfiles)
        {
            parts.Add("All remaining profiles: read-only");
        }

        return string.Join(". ", parts);
    }

    private static string BuildLayoutDetail(SalesforceConfigRequirement requirement)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(requirement.ReplaceFieldApiName))
        {
            parts.Add($"Replace field: {requirement.ReplaceFieldApiName} -> {requirement.FieldApiName}");
        }
        else if (!string.IsNullOrWhiteSpace(requirement.FieldApiName))
        {
            parts.Add($"Field: {requirement.ObjectApiName}.{requirement.FieldApiName}");
        }

        if (!string.IsNullOrWhiteSpace(requirement.TargetSectionLabel))
        {
            parts.Add($"Section: {requirement.TargetSectionLabel}");
        }

        if (!string.IsNullOrWhiteSpace(requirement.Description))
        {
            parts.Add(requirement.Description);
        }

        return string.Join(". ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string BuildFlexipageDetail(SalesforceConfigRequirement requirement)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(requirement.ReplaceFieldApiName))
        {
            parts.Add($"Replace field reference: {requirement.ReplaceFieldApiName} -> {requirement.FieldApiName}");
        }
        else if (!string.IsNullOrWhiteSpace(requirement.FieldApiName))
        {
            parts.Add($"Field: {requirement.ObjectApiName}.{requirement.FieldApiName}");
        }

        if (!string.IsNullOrWhiteSpace(requirement.VisibilityConditionSummary))
        {
            parts.Add($"Visibility: {requirement.VisibilityConditionSummary}");
        }

        if (!string.IsNullOrWhiteSpace(requirement.Description))
        {
            parts.Add(requirement.Description);
        }

        return string.Join(". ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }
    private static string BuildPermissionSetDetail(SalesforceConfigRequirement requirement)
    {
        var parts = new List<string>();

        if (requirement.PermissionSetNames.Count > 0)
        {
            parts.Add($"Permission sets: {string.Join(", ", requirement.PermissionSetNames)}");
        }

        if (!string.IsNullOrWhiteSpace(requirement.Description))
        {
            parts.Add(requirement.Description);
        }

        return string.Join(". ", parts);
    }

    private static string FirstNonBlank(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }
}


