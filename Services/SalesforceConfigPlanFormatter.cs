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
            "profile_metadata" or "profile_fls_update" => $"Profile: {req.TargetMetadataName ?? "Multiple"}",
            "permission_set" or "permission_set_fls_update" => $"Permission Set: {string.Join(", ", req.PermissionSetNames)}",
            "custom_permission" => $"Custom Permission: {req.Label ?? req.TargetMetadataName}",
            "unsupported_requirement" => $"[UNSUPPORTED] {req.Label ?? req.Description}",
            _ => req.Label ?? req.Description ?? req.Id
        };
    }

    public static string BuildRequirementDetail(SalesforceConfigRequirement req)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(req.PermissionType)) sb.Append($"Type: {req.PermissionType}. ");
        if (!string.IsNullOrWhiteSpace(req.ObjectApiName)) sb.Append($"Object: {req.ObjectApiName}. ");
        if (!string.IsNullOrWhiteSpace(req.FieldApiName)) sb.Append($"Field: {req.FieldApiName}. ");
        if (!string.IsNullOrWhiteSpace(req.PermissionValue)) sb.Append($"Value: {req.PermissionValue}. ");
        if (!string.IsNullOrWhiteSpace(req.Description)) sb.Append(req.Description);
        
        return sb.ToString().Trim();
    }
}
