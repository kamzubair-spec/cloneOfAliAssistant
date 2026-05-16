using eZBERP_AI_IDE.Models;

namespace eZBERP_AI_IDE.Services;

public interface IConfigWorkItemHandler
{
    string ServiceName { get; }
    bool CanHandle(SalesforceConfigRequirement requirement);
    Task<FileChangeSet?> BuildChangeSetAsync(string repoPath, SalesforceConfigRequirement requirement);
}

public interface IRepositoryAwareConfigWorkItemHandler : IConfigWorkItemHandler
{
    bool CanHandle(string repoPath, SalesforceConfigRequirement requirement);
    string BuildCannotHandleReason(string repoPath, SalesforceConfigRequirement requirement);
}