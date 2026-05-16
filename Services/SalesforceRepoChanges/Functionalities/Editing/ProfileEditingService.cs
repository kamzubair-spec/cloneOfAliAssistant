using eZBERP_AI_IDE.Models;
using File = System.IO.File;

namespace eZBERP_AI_IDE.Services;

public sealed class ProfileEditingService : IRepositoryAwareConfigWorkItemHandler
{
    private readonly SalesforcePermissionEditingToolkit _toolkit = new();

    public string ServiceName => nameof(ProfileEditingService);

    public bool CanHandle(SalesforceConfigRequirement requirement)
    {
        return PermissionToolingCatalog.IsProfileRequirement(requirement.Type)
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
        var targetPaths = await _toolkit.ResolveProfilePathsAsync(repoPath, requirement);

        foreach (var path in targetPaths)
        {
            var scopedRequirement = _toolkit.ScopeProfileRequirementForTarget(path, requirement);
            var existingContent = File.Exists(path)
                ? await File.ReadAllTextAsync(path)
                : _toolkit.BuildProfileDefaultXml();
            var proposedContent = await Task.Run(() => _toolkit.ProcessSurgicalEdit(existingContent, scopedRequirement));

            proposals.Add(new FileChangeProposal(
                Path.GetRelativePath(repoPath, path),
                existingContent,
                proposedContent,
                File.Exists(path)));
        }

        return new FileChangeSet($"Profile updates for {requirement.Id}", proposals);
    }
}
