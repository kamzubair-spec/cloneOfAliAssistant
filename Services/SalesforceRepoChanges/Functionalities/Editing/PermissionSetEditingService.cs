using eZBERP_AI_IDE.Models;
using File = System.IO.File;

namespace eZBERP_AI_IDE.Services;

public sealed class PermissionSetEditingService : IRepositoryAwareConfigWorkItemHandler
{
    private readonly SalesforcePermissionEditingToolkit _toolkit = new();

    public string ServiceName => nameof(PermissionSetEditingService);

    public bool CanHandle(SalesforceConfigRequirement requirement)
    {
        return PermissionToolingCatalog.IsPermissionSetRequirement(requirement.Type)
               && PermissionToolingCatalog.IsSupportedPermissionType(requirement.PermissionType);
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

    public async Task<FileChangeSet?> BuildChangeSetAsync(string repoPath, SalesforceConfigRequirement requirement)
    {
        if (!CanHandle(repoPath, requirement))
        {
            return null;
        }

        var proposals = new List<FileChangeProposal>();
        var targetPaths = await _toolkit.ResolvePermissionSetPathsAsync(repoPath, requirement);

        foreach (var path in targetPaths)
        {
            var existingContent = File.Exists(path)
                ? await File.ReadAllTextAsync(path)
                : _toolkit.BuildPermissionSetDefaultXml(requirement, path);
            var proposedContent = await Task.Run(() => _toolkit.ProcessSurgicalEdit(existingContent, requirement));

            proposals.Add(new FileChangeProposal(
                Path.GetRelativePath(repoPath, path),
                existingContent,
                proposedContent,
                File.Exists(path)));
        }

        return new FileChangeSet($"Permission set updates for {requirement.Id}", proposals);
    }
}
