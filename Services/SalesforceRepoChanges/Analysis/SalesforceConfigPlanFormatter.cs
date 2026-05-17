using System.Text;
using eZBERP_AI_IDE.Models;

namespace eZBERP_AI_IDE.Services;

public static class SalesforceConfigPlanFormatter
{
    public static string BuildPreview(SalesforceConfigPlan plan)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Salesforce Permission Roadmap");
        builder.AppendLine(new string('=', 80));
        builder.AppendLine();
        plan ??= new SalesforceConfigPlan();

        builder.AppendLine(string.IsNullOrWhiteSpace(plan.Summary) ? "No summary provided." : plan.Summary.Trim());

        if (plan.Questions?.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Open questions / assumptions");
            foreach (var q in plan.Questions.Where(q => !string.IsNullOrWhiteSpace(q))) builder.AppendLine($"- {q.Trim()}");
        }

        builder.AppendLine();
        builder.AppendLine("Planned permission work");
        if (plan.Requirements?.Count == 0)
        {
            builder.AppendLine("- No actionable permission items extracted.");
            return builder.ToString();
        }

        foreach (var req in plan.Requirements!)
        {
            builder.AppendLine($"- [{req.Type}] {BuildRequirementHeadline(req)}");
            var detail = BuildRequirementDetail(req);
            if (!string.IsNullOrWhiteSpace(detail)) builder.AppendLine($"  {detail}");
        }

        return builder.ToString();
    }

    public static string BuildCoveragePreview(SalesforceConfigCoverage coverage)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Salesforce Permission Coverage");
        builder.AppendLine(new string('=', 80));
        builder.AppendLine();
        coverage ??= new SalesforceConfigCoverage();
        var results = coverage.Results ?? new List<RequirementCoverageResult>();
        builder.AppendLine($"Coverage: {results.Count(r => r.IsSupported)} of {results.Count} requirements supported");
        builder.AppendLine();

        foreach (var result in results)
        {
            builder.AppendLine($"{(result.IsSupported ? "[OK]" : "[NO]")} {BuildRequirementHeadline(result.Requirement)}");
            builder.AppendLine($"     {result.Reason}");
        }

        return builder.ToString();
    }

    public static string BuildRequirementHeadline(SalesforceConfigRequirement req)
    {
        return req.Type switch
        {
            "profile_metadata" or "profile_fls_update" => BuildProfileHeadline(req),
            "permission_set" or "permission_set_fls_update" => BuildPermissionSetHeadline(req),
            "custom_permission" => BuildCustomPermissionHeadline(req),
            "custom_field" or "field_metadata" => BuildFieldHeadline(req),
            "unsupported_requirement" => BuildUnsupportedHeadline(req),
            _ => req.Label ?? req.Description ?? req.Id
        };
    }

    public static string BuildRequirementDetail(SalesforceConfigRequirement req)
    {
        if (req.Type.Equals("unsupported_requirement", StringComparison.OrdinalIgnoreCase))
        {
            var context = BuildUnsupportedContext(req);
            return string.IsNullOrWhiteSpace(context)
                ? PermissionToolingCatalog.UnsupportedRequirementMessage
                : $"{context} {PermissionToolingCatalog.UnsupportedRequirementMessage}";
        }

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(req.Operation)) sb.Append($"Operation: {req.Operation}. ");
        if (!string.IsNullOrWhiteSpace(req.FieldType)) sb.Append($"Field type: {req.FieldType}. ");
        if (!string.IsNullOrWhiteSpace(req.PermissionType)) sb.Append($"Permission type: {req.PermissionType}. ");
        if (!string.IsNullOrWhiteSpace(req.ObjectApiName)) sb.Append($"Object: {req.ObjectApiName}. ");
        if (!string.IsNullOrWhiteSpace(req.FieldApiName)) sb.Append($"Field: {req.FieldApiName}. ");
        if (!string.IsNullOrWhiteSpace(req.GlobalValueSetName)) sb.Append($"Global value set: {req.GlobalValueSetName}. ");
        if (!string.IsNullOrWhiteSpace(req.ControllingFieldApiName)) sb.Append($"Controlling field: {req.ControllingFieldApiName}. ");
        if (!string.IsNullOrWhiteSpace(req.RelationshipTargetObject)) sb.Append($"Related object: {req.RelationshipTargetObject}. ");
        if (req.RecordTypeNames?.Count > 0) sb.Append($"Record types: {string.Join(", ", req.RecordTypeNames)}. ");
        if (!string.IsNullOrWhiteSpace(req.PermissionValue)) sb.Append($"Value: {req.PermissionValue}. ");
        if (req.PermissionSetNames?.Count > 1) sb.Append($"Targets: {string.Join(", ", req.PermissionSetNames.Where(name => !string.IsNullOrWhiteSpace(name)))}. ");
        if (!string.IsNullOrWhiteSpace(req.Description)) sb.Append(req.Description);
        
        return sb.ToString().Trim();
    }

    private static string BuildProfileHeadline(SalesforceConfigRequirement req)
    {
        var target = string.IsNullOrWhiteSpace(req.TargetMetadataName) ? "Multiple" : req.TargetMetadataName;
        var action = BuildPermissionAction(req);
        return string.IsNullOrWhiteSpace(action)
            ? $"Profile: {target}"
            : $"Profile: {target} - {action}";
    }

    private static string BuildPermissionSetHeadline(SalesforceConfigRequirement req)
    {
        var target = BuildPermissionSetTarget(req);
        var action = BuildPermissionAction(req);
        return string.IsNullOrWhiteSpace(action)
            ? $"Permission Set: {target}"
            : $"Permission Set: {target} - {action}";
    }

    private static string BuildCustomPermissionHeadline(SalesforceConfigRequirement req)
    {
        var name = FirstNonBlank(req.Label, req.TargetMetadataName, "Unnamed Custom Permission");
        var operation = string.IsNullOrWhiteSpace(req.Operation) ? string.Empty : $"{req.Operation} ";
        return $"Custom Permission: {operation}{name}".Trim();
    }

    private static string BuildFieldHeadline(SalesforceConfigRequirement req)
    {
        var fieldPath = BuildFieldPath(req);
        var operation = string.IsNullOrWhiteSpace(req.Operation) ? "update" : req.Operation;
        var fieldType = string.IsNullOrWhiteSpace(req.FieldType) ? "field" : req.FieldType;
        return $"Field: {operation} {fieldPath} ({fieldType})";
    }

    private static string BuildUnsupportedHeadline(SalesforceConfigRequirement req)
    {
        var label = BuildUnsupportedLabel(req);
        return string.IsNullOrWhiteSpace(label)
            ? "[UNSUPPORTED]"
            : $"[UNSUPPORTED] {label}";
    }

    private static string BuildPermissionSetTarget(SalesforceConfigRequirement req)
    {
        var names = req.PermissionSetNames?.Where(name => !string.IsNullOrWhiteSpace(name)).ToList() ?? new List<string>();
        if (names.Count > 0)
        {
            return string.Join(", ", names);
        }

        return string.IsNullOrWhiteSpace(req.TargetMetadataName) ? "Multiple" : req.TargetMetadataName;
    }

    private static string BuildPermissionAction(SalesforceConfigRequirement req)
    {
        return req.PermissionType?.ToLowerInvariant() switch
        {
            "fls" => BuildFlsAction(req),
            "tab" => $"set tab visibility to {req.PermissionValue}",
            "apex_class" => $"set Apex class access for {FirstNonBlank(req.TargetMetadataName, req.Label, "class")} to {req.PermissionValue}",
            "apex_page" => $"set Apex page access for {FirstNonBlank(req.TargetMetadataName, req.Label, "page")} to {req.PermissionValue}",
            "object" => $"set object permissions on {FirstNonBlank(req.ObjectApiName, "object")} to {req.PermissionValue}",
            "custom_permission" => $"set custom permission {FirstNonBlank(req.TargetMetadataName, req.Label, "permission")} to {req.PermissionValue}",
            "record_type" => $"set record type visibility for {FirstNonBlank(req.TargetMetadataName, req.Label, "record type")} to {req.PermissionValue}",
            "application" => $"set app visibility for {FirstNonBlank(req.TargetMetadataName, req.Label, "application")} to {req.PermissionValue}",
            "user_permission" => $"set user permission {FirstNonBlank(req.TargetMetadataName, req.Label, "permission")} to {req.PermissionValue}",
            _ => string.Empty
        };
    }

    private static string BuildFlsAction(SalesforceConfigRequirement req)
    {
        var fieldPath = BuildFieldPath(req);
        if (string.IsNullOrWhiteSpace(fieldPath))
        {
            return "update field-level security";
        }

        var access = req.PermissionValue?.Equals("true", StringComparison.OrdinalIgnoreCase) == true
            ? "read/edit access"
            : req.PermissionValue?.Equals("false", StringComparison.OrdinalIgnoreCase) == true
                ? "read-only access"
                : $"access = {req.PermissionValue}";

        return $"grant {access} on {fieldPath}";
    }

    private static string BuildUnsupportedLabel(SalesforceConfigRequirement req)
    {
        if (!string.IsNullOrWhiteSpace(req.ValidationRuleName))
        {
            return $"Validation Rule: {req.ValidationRuleName}";
        }

        if (!string.IsNullOrWhiteSpace(req.Formula))
        {
            return $"Formula Update: {FirstNonBlank(req.Label, BuildFieldPath(req), req.TargetMetadataName)}";
        }

        if (!string.IsNullOrWhiteSpace(req.TargetLayoutOrPageLabel) || !string.IsNullOrWhiteSpace(req.TargetSectionLabel))
        {
            return $"Layout/Page Update: {FirstNonBlank(req.TargetLayoutOrPageLabel, req.TargetSectionLabel, req.Label, req.TargetMetadataName)}";
        }

        if (req.PicklistEntries?.Count > 0 || req.PicklistValues?.Count > 0 || req.PicklistRenames?.Count > 0)
        {
            return $"Picklist Update: {FirstNonBlank(req.Label, BuildFieldPath(req), req.TargetMetadataName)}";
        }

        return FirstNonBlank(req.Label, req.TargetMetadataName, req.Description);
    }

    private static string BuildUnsupportedContext(SalesforceConfigRequirement req)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(req.ObjectApiName)) parts.Add($"Object: {req.ObjectApiName}.");
        if (!string.IsNullOrWhiteSpace(req.FieldApiName)) parts.Add($"Field: {req.FieldApiName}.");
        if (!string.IsNullOrWhiteSpace(req.TargetLayoutOrPageLabel)) parts.Add($"Layout/Page: {req.TargetLayoutOrPageLabel}.");
        if (!string.IsNullOrWhiteSpace(req.TargetSectionLabel)) parts.Add($"Section: {req.TargetSectionLabel}.");
        if (!string.IsNullOrWhiteSpace(req.ErrorLocation)) parts.Add($"Error location: {req.ErrorLocation}.");
        if (!string.IsNullOrWhiteSpace(req.ErrorMessage)) parts.Add($"Error message: {req.ErrorMessage}.");
        if (!string.IsNullOrWhiteSpace(req.Description) && !req.Description.Equals(PermissionToolingCatalog.UnsupportedRequirementMessage, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(req.Description.Trim());
        }

        return string.Join(" ", parts);
    }

    private static string BuildFieldPath(SalesforceConfigRequirement req)
    {
        if (!string.IsNullOrWhiteSpace(req.ObjectApiName) && !string.IsNullOrWhiteSpace(req.FieldApiName))
        {
            return $"{req.ObjectApiName}.{req.FieldApiName}";
        }

        return FirstNonBlank(req.FieldApiName, req.TargetMetadataName);
    }

    private static string FirstNonBlank(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }
}
