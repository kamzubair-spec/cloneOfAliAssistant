using eZBERP_AI_IDE.Models;

namespace eZBERP_AI_IDE.Services;

public sealed class CoverageAssessmentService
{
    private readonly IReadOnlyList<IConfigWorkItemHandler> _handlers;
    private readonly DeepSeekClient _deepSeekClient;

    public CoverageAssessmentService(IEnumerable<IConfigWorkItemHandler> handlers, DeepSeekClient deepSeekClient)
    {
        _handlers = handlers.ToList();
        _deepSeekClient = deepSeekClient;
    }

    public SalesforceConfigCoverage Assess(SalesforceConfigPlan plan) => Assess(string.Empty, plan);
    public SalesforceConfigCoverage Assess(string repoPath, SalesforceConfigPlan plan) => AssessAsync(repoPath, plan).GetAwaiter().GetResult();
    public Task<SalesforceConfigCoverage> AssessAsync(SalesforceConfigPlan plan) => AssessAsync(string.Empty, plan);

    public async Task<SalesforceConfigCoverage> AssessAsync(string repoPath, SalesforceConfigPlan plan)
    {
        plan ??= new SalesforceConfigPlan();
        var results = new List<RequirementCoverageResult>();
        var supportedPlan = new SalesforceConfigPlan { Summary = plan.Summary };

        foreach (var requirement in plan.Requirements)
        {
            var handler = _handlers.FirstOrDefault(h => CanHandle(h, repoPath, requirement));
            var isSupported = handler is not null;
            var reason = isSupported ? $"Supported by {handler!.ServiceName}." : BuildReason(repoPath, requirement);

            if (isSupported) supportedPlan.Requirements.Add(requirement);

            results.Add(new RequirementCoverageResult
            {
                Requirement = requirement,
                IsSupported = isSupported,
                Reason = reason
            });
        }

        return new SalesforceConfigCoverage
        {
            OriginalPlan = plan,
            SupportedPlan = supportedPlan,
            Results = results
        };
    }

    private bool CanHandle(IConfigWorkItemHandler handler, string repoPath, SalesforceConfigRequirement requirement)
    {
        if (!handler.CanHandle(requirement)) return false;
        return handler is not IRepositoryAwareConfigWorkItemHandler repositoryAware
               || string.IsNullOrWhiteSpace(repoPath)
               || repositoryAware.CanHandle(repoPath, requirement);
    }

    private string BuildReason(string repoPath, SalesforceConfigRequirement requirement)
    {
        var repositoryAwareHandler = _handlers.OfType<IRepositoryAwareConfigWorkItemHandler>().FirstOrDefault(h => h.CanHandle(requirement));
        if (repositoryAwareHandler != null && !string.IsNullOrWhiteSpace(repoPath)) return repositoryAwareHandler.BuildCannotHandleReason(repoPath, requirement);
        return "Not supported.";
    }
}
