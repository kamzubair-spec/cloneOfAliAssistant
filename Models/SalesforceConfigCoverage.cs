namespace eZBERP_AI_IDE.Models;

public sealed class SalesforceConfigCoverage
{
    public SalesforceConfigPlan OriginalPlan { get; init; } = new();
    public SalesforceConfigPlan SupportedPlan { get; init; } = new();
    public SalesforceConfigPlan AlternativePlan { get; init; } = new();
    public List<RequirementCoverageResult> Results { get; init; } = new();

    public int TotalRequirements => Results.Count;
    public int SupportedRequirements => Results.Count(result => result.IsSupported);
    public int AlternativeRequirements => Results.Count(result => !result.IsSupported && result.AlternativeRequirement is not null);
    public int UnsupportedRequirements => Results.Count(result => !result.IsSupported);
    public int CoveragePercentage => TotalRequirements == 0
        ? 0
        : (int)Math.Round((decimal)SupportedRequirements / TotalRequirements * 100, MidpointRounding.AwayFromZero);
}

public sealed class RequirementCoverageResult
{
    public SalesforceConfigRequirement Requirement { get; init; } = new();
    public bool IsSupported { get; init; }
    public string Reason { get; init; } = string.Empty;
    public SalesforceConfigRequirement? AlternativeRequirement { get; init; }
    public string AlternativeReason { get; init; } = string.Empty;
}
