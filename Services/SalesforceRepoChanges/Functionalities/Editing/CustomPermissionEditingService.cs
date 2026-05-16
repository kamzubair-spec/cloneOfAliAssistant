using eZBERP_AI_IDE.Models;

namespace eZBERP_AI_IDE.Services;

public sealed class CustomPermissionEditingService : IRepositoryAwareConfigWorkItemHandler
{
    private readonly SalesforcePermissionEditingToolkit _toolkit = new();

    public string ServiceName => nameof(CustomPermissionEditingService);

    public bool CanHandle(SalesforceConfigRequirement requirement)
    {
        return PermissionToolingCatalog.IsCustomPermissionRequirement(requirement.Type);
    }

    public bool CanHandle(string repoPath, SalesforceConfigRequirement requirement)
    {
        if (!CanHandle(requirement))
        {
            return false;
        }

        var baseDir = Path.Combine(repoPath, "force-app", "main", "default");
        return Directory.Exists(baseDir);
    }

    public string BuildCannotHandleReason(string repoPath, SalesforceConfigRequirement requirement)
    {
        return PermissionToolingCatalog.UnsupportedRequirementMessage;
    }

    public Task<FileChangeSet?> BuildChangeSetAsync(string repoPath, SalesforceConfigRequirement requirement)
    {
        if (!CanHandle(repoPath, requirement))
        {
            return Task.FromResult<FileChangeSet?>(null);
        }

        return _toolkit.BuildCustomPermissionChangeSetAsync(repoPath, requirement);
    }
}
