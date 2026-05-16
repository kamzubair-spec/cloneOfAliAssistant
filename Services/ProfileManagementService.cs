using eZBERP_AI_IDE.Models;

namespace eZBERP_AI_IDE.Services;

public sealed class ProfileManagementService : IRepositoryAwareConfigWorkItemHandler
{
    private readonly ProfileFlsToolService _profileFlsToolService;

    public ProfileManagementService(ProfileFlsToolService profileFlsToolService)
    {
        _profileFlsToolService = profileFlsToolService;
    }

    public string ServiceName => nameof(ProfileManagementService);

    public bool CanHandle(SalesforceConfigRequirement requirement)
    {
        return requirement.Type.Equals("profile_fls_update", StringComparison.OrdinalIgnoreCase);
    }

    public bool CanHandle(string repoPath, SalesforceConfigRequirement requirement)
    {
        if (!CanHandle(requirement))
        {
            return false;
        }

        return ResolveTargetProfileCount(repoPath, requirement) > 0;
    }

    public string BuildCannotHandleReason(string repoPath, SalesforceConfigRequirement requirement)
    {
        if (ResolveTargetProfileCount(repoPath, requirement) == 0)
        {
            return "No matching profile files were found for the requested FLS update.";
        }

        return "Profile FLS requirement is supported.";
    }

    public async Task<FileChangeSet?> BuildChangeSetAsync(string repoPath, SalesforceConfigRequirement requirement)
    {
        if (string.IsNullOrWhiteSpace(requirement.ObjectApiName) || string.IsNullOrWhiteSpace(requirement.FieldApiName))
        {
            throw new InvalidOperationException("Profile FLS changes require both objectApiName and fieldApiName.");
        }

        var access = requirement.ProfileAccess ?? new ProfileAccessRequirement();
        var request = new ProfileFlsRequest(
            requirement.ObjectApiName,
            requirement.FieldApiName,
            access.EditableProfiles,
            access.ReadOnlyProfiles,
            access.ApplyReadOnlyToRemainingProfiles);

        return await _profileFlsToolService.BuildChangeSetAsync(repoPath, request);
    }

    private static int ResolveTargetProfileCount(string repoPath, SalesforceConfigRequirement requirement)
    {
        var profilesDirectory = Path.Combine(repoPath, "force-app", "main", "default", "profiles");
        if (!Directory.Exists(profilesDirectory))
        {
            return 0;
        }

        var profilePaths = Directory.GetFiles(profilesDirectory, "*.profile-meta.xml");
        var access = requirement.ProfileAccess ?? new ProfileAccessRequirement();
        var requested = access.EditableProfiles.Concat(access.ReadOnlyProfiles).Where(name => !string.IsNullOrWhiteSpace(name)).ToList();
        if (access.ApplyReadOnlyToRemainingProfiles)
        {
            return profilePaths.Length;
        }

        return requested.Count(name => profilePaths.Any(path => NormalizeProfileName(Path.GetFileName(path)) == NormalizeProfileName(name)
                                                               || NormalizeProfileName(Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path))) == NormalizeProfileName(name)));
    }

    private static string NormalizeProfileName(string value)
    {
        return System.Text.RegularExpressions.Regex.Replace(value ?? string.Empty, @"[\s_\-\.]", string.Empty).ToLowerInvariant()
            .Replace("profilemetaxml", string.Empty)
            .Replace("profilemeta", string.Empty)
            .Replace("profile", string.Empty);
    }
}
