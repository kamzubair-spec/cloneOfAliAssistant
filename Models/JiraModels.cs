namespace eZBERP_AI_IDE.Models;

public sealed class JiraWorkItem
{
    public string Key { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string Sprint { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string FixVersions { get; init; } = string.Empty;
    public string Assignee { get; init; } = string.Empty;
    public string StoryPoints { get; init; } = string.Empty;
}

public sealed class JiraStoryFilter
{
    public string SpaceOrProject { get; init; } = "Phoenix";
    public string IssueType { get; init; } = "Story";
    public string LeadConsultant { get; init; } = "Current User";
    public string Sprint { get; init; } = "Phoenix_94.0_Sprint 1";
    public string Status { get; init; } = string.Empty;
    public string SearchText { get; init; } = string.Empty;
    public int MaxResults { get; init; } = 50;
}
