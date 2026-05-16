using eZBERP_AI_IDE.Models;

namespace eZBERP_AI_IDE.Services;

public sealed class CoverageAssessmentService
{
    private readonly IReadOnlyList<IConfigWorkItemHandler> _handlers;
    private readonly AlternativeImplementationService _alternativeImplementationService;

    public CoverageAssessmentService(IEnumerable<IConfigWorkItemHandler> handlers, DeepSeekClient deepSeekClient)
    {
        _handlers = handlers.ToList();
        _alternativeImplementationService = new AlternativeImplementationService(deepSeekClient);
    }

    public SalesforceConfigCoverage Assess(SalesforceConfigPlan plan)
    {
        return AssessAsync(string.Empty, plan).GetAwaiter().GetResult();
    }

    public SalesforceConfigCoverage Assess(string repoPath, SalesforceConfigPlan plan)
    {
        return AssessAsync(repoPath, plan).GetAwaiter().GetResult();
    }

    public Task<SalesforceConfigCoverage> AssessAsync(SalesforceConfigPlan plan)
    {
        return AssessAsync(string.Empty, plan);
    }

    public async Task<SalesforceConfigCoverage> AssessAsync(string repoPath, SalesforceConfigPlan plan)
    {
        plan ??= new SalesforceConfigPlan();
        plan.Summary ??= string.Empty;
        plan.Requirements ??= new List<SalesforceConfigRequirement>();
        plan.Questions ??= new List<string>();

        var supportedPlan = new SalesforceConfigPlan
        {
            Summary = plan.Summary,
            Questions = plan.Questions
        };
        var alternativePlan = new SalesforceConfigPlan
        {
            Summary = plan.Summary,
            Questions = plan.Questions
        };

        var results = new List<RequirementCoverageResult>();

        foreach (var requirement in plan.Requirements)
        {
            NormalizeFlowFirstRequirement(requirement);

            var preflightReason = BuildPreflightUnsupportedReason(requirement);
            var handler = string.IsNullOrWhiteSpace(preflightReason)
                ? _handlers.FirstOrDefault(item => CanHandlerProcess(item, repoPath, requirement))
                : null;
            var isSupported = handler is not null;
            var reason = isSupported
                ? $"Supported by {handler!.ServiceName}."
                : FirstNonBlank(preflightReason, BuildReason(repoPath, requirement));
            var alternative = isSupported
                ? null
                : await _alternativeImplementationService.BuildAlternativeAsync(requirement, reason);

            if (isSupported)
            {
                supportedPlan.Requirements.Add(requirement);
            }
            else if (alternative is not null)
            {
                alternativePlan.Requirements.Add(alternative.Requirement);
            }

            results.Add(new RequirementCoverageResult
            {
                Requirement = requirement,
                IsSupported = isSupported,
                Reason = reason,
                AlternativeRequirement = alternative?.Requirement,
                AlternativeReason = alternative?.Reason ?? string.Empty
            });
        }

        return new SalesforceConfigCoverage
        {
            OriginalPlan = plan,
            SupportedPlan = supportedPlan,
            AlternativePlan = alternativePlan,
            Results = results
        };
    }

    private bool CanHandlerProcess(IConfigWorkItemHandler handler, string repoPath, SalesforceConfigRequirement requirement)
    {
        if (!handler.CanHandle(requirement))
        {
            return false;
        }

        return handler is not IRepositoryAwareConfigWorkItemHandler repositoryAware
               || string.IsNullOrWhiteSpace(repoPath)
               || repositoryAware.CanHandle(repoPath, requirement);
    }

    private static string BuildPreflightUnsupportedReason(SalesforceConfigRequirement requirement)
    {
        if (!string.Equals(requirement.Type, "implementation_code", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (HasActionableCodeIntent(requirement))
        {
            return string.Empty;
        }

        return "The analyzer identified a code change, but it did not provide enough detail to safely route or edit code. Re-analyze the story or add more explicit implementation detail.";
    }

    private static bool HasActionableCodeIntent(SalesforceConfigRequirement requirement)
    {
        var readableWork = FirstNonBlank(
            requirement.Label,
            requirement.Description,
            requirement.Operation,
            requirement.ImplementationStrategy);

        var implementationHints = new[]
        {
            requirement.ObjectApiName,
            requirement.FieldApiName,
            requirement.ImplementationKind,
            requirement.SuggestedTriggerEvent,
            requirement.SuggestedHelperMethodName,
            requirement.EventInvocation,
            requirement.HelperMethodCode,
            requirement.TestMethodCode
        };

        return !string.IsNullOrWhiteSpace(readableWork)
               && (implementationHints.Any(value => !string.IsNullOrWhiteSpace(value))
                   || requirement.SuggestedFiles.Any(value => !string.IsNullOrWhiteSpace(value))
                   || requirement.RequiresSecondAiPass);
    }

    private static void NormalizeFlowFirstRequirement(SalesforceConfigRequirement requirement)
    {
        if (!string.Equals(requirement.Type, "implementation_code", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var combined = string.Join(" ", new[]
        {
            requirement.Label,
            requirement.Description,
            requirement.Operation,
            requirement.ObjectApiName,
            requirement.FieldApiName
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        if (!LooksLikeFlowWork(combined) || LooksLikeExplicitCodeRequest(combined))
        {
            return;
        }

        requirement.Type = "flow";
        requirement.Service = "FlowManagementService";
        requirement.ImplementationKind = string.Empty;
        requirement.ImplementationStrategy = string.Empty;
        requirement.SuggestedFiles.Clear();
    }

    private static bool LooksLikeFlowWork(string value)
    {
        return ContainsAny(value, "flow", "screen flow", "record-triggered", "record triggered", "created via a flow", "flow logic");
    }

    private static bool LooksLikeExplicitCodeRequest(string value)
    {
        return ContainsAny(value, "apex", "trigger", "trigger handler", "lwc", "aura", "visualforce", "javascript", ".cls", ".trigger", ".js", ".html");
    }

    private string BuildReason(string repoPath, SalesforceConfigRequirement requirement)
    {
        var repositoryAwareHandler = _handlers
            .OfType<IRepositoryAwareConfigWorkItemHandler>()
            .FirstOrDefault(handler => handler.CanHandle(requirement));

        if (repositoryAwareHandler is not null && !string.IsNullOrWhiteSpace(repoPath))
        {
            return repositoryAwareHandler.BuildCannotHandleReason(repoPath, requirement);
        }

        return BuildUnsupportedReason(requirement);
    }


    private static string BuildValidationRuleUnsupportedReason(SalesforceConfigRequirement requirement)
    {
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(requirement.ObjectApiName))
        {
            missing.Add("object API name");
        }

        if (string.IsNullOrWhiteSpace(FirstNonBlank(requirement.ValidationRuleName, requirement.Label)))
        {
            missing.Add("validation rule name");
        }

        if (string.IsNullOrWhiteSpace(requirement.Formula))
        {
            missing.Add("error condition formula");
        }

        return missing.Count == 0
            ? "Validation rule support exists, but this requirement could not be matched to ObjectManagementService."
            : $"Validation rule support exists, but the extracted requirement is missing: {string.Join(", ", missing)}.";
    }

    private static string FirstNonBlank(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static bool ContainsAny(string source, params string[] values)
    {
        return values.Any(value => source.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildUnsupportedReason(SalesforceConfigRequirement requirement)
    {
        return requirement.Type switch
        {
            "external_dependency" => "This requirement is explicitly outside the supported change set for this app.",
            "implementation_code" => "No CodeEditService is registered to handle surgical code changes.",
            "unsupported_requirement" => "No deterministic service is available for this requirement yet.",
            "layout" => "No existing layout metadata target could be resolved for this requirement.",
            "flexipage" => "No existing flexipage metadata target could be resolved for this requirement.",
            "quick_action" => "Only existing quick action layout updates are supported. Creating new quick actions or unresolved quick action targets still need manual handling.",
            "flow" => "No FlowManagementService is implemented yet.",
            "validation_rule" => BuildValidationRuleUnsupportedReason(requirement),
            "record_type" => "Record type support is limited to existing record type picklist value updates with a resolved record type file.",
            "picklist" => "Picklist support is limited to local valueSetDefinition fields. Global value sets need type global_value_set.",
            "permission_set" => "No matching permission set files were found or the permission set request is incomplete.",
            "custom_metadata" => "Custom metadata support exists, but this requirement needs a custom metadata type, record developer name, and concrete field/value pairs.",
            "custom_permission" => "Custom permission changes need a metadata name and can create or update an existing custom permission file.",
            "global_value_set" => "Global value set changes need an existing global value set metadata file and values to add or labels to rename.",
            "custom_label" => "Custom label changes need a label API name and value.",
            _ => string.IsNullOrWhiteSpace(requirement.Service)
                ? $"No deterministic config service is mapped for requirement type '{requirement.Type}'."
                : $"The mapped service '{requirement.Service}' is not implemented or cannot handle this requirement."
        };
    }
}




