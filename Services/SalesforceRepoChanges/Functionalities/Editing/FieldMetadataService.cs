using eZBERP_AI_IDE.Models;
using File = System.IO.File;

namespace eZBERP_AI_IDE.Services;

public sealed class FieldMetadataService : IRepositoryAwareConfigWorkItemHandler
{
    private readonly MetadataDiscoveryService _metadataDiscoveryService = new();
    private readonly SalesforceFieldEditingToolkit _toolkit = new();

    public string ServiceName => nameof(FieldMetadataService);

    public bool CanHandle(SalesforceConfigRequirement requirement)
    {
        return FieldMetadataCatalog.IsSupportedRequirementType(requirement.Type);
    }

    public bool CanHandle(string repoPath, SalesforceConfigRequirement requirement)
    {
        if (!CanHandle(requirement)
            || requirement.NeedsUserConfirmation
            || !requirement.IsResolved
            || string.IsNullOrWhiteSpace(requirement.ObjectApiName)
            || string.IsNullOrWhiteSpace(requirement.FieldApiName))
        {
            return false;
        }

        if (!_metadataDiscoveryService.ObjectExists(repoPath, requirement.ObjectApiName))
        {
            return false;
        }

        var path = _toolkit.GetFieldPath(repoPath, requirement);
        if (requirement.Operation.Equals("update", StringComparison.OrdinalIgnoreCase) && !File.Exists(path))
        {
            return false;
        }

        if (requirement.Operation.Equals("create", StringComparison.OrdinalIgnoreCase) && !FieldMetadataCatalog.IsSupportedCreateFieldType(requirement.FieldType))
        {
            return false;
        }

        if (FieldMetadataCatalog.RequiresValueSetSource(requirement.FieldType))
        {
            if (string.IsNullOrWhiteSpace(requirement.ValueSetSource))
            {
                return false;
            }

            if (requirement.ValueSetSource.Equals("global", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(requirement.GlobalValueSetName))
            {
                return false;
            }
        }

        if (FieldMetadataCatalog.RequiresRelationshipTarget(requirement.FieldType)
            && string.IsNullOrWhiteSpace(requirement.RelationshipTargetObject))
        {
            return false;
        }

        var existingType = _toolkit.GetExistingFieldType(path);
        return existingType is null || FieldMetadataCatalog.IsCompatibleFieldUpdate(existingType, requirement.FieldType);
    }

    public string BuildCannotHandleReason(string repoPath, SalesforceConfigRequirement requirement)
    {
        if (requirement.NeedsUserConfirmation || !requirement.IsResolved)
        {
            return string.IsNullOrWhiteSpace(requirement.AmbiguityReason)
                ? "Requirement analysis is still waiting for user confirmation."
                : requirement.AmbiguityReason;
        }

        if (string.IsNullOrWhiteSpace(requirement.ObjectApiName) || !_metadataDiscoveryService.ObjectExists(repoPath, requirement.ObjectApiName))
        {
            return "Target object could not be resolved.";
        }

        if (requirement.Operation.Equals("create", StringComparison.OrdinalIgnoreCase) && !FieldMetadataCatalog.IsSupportedCreateFieldType(requirement.FieldType))
        {
            return $"Field type '{requirement.FieldType}' is not supported for creation.";
        }

        if (FieldMetadataCatalog.RequiresValueSetSource(requirement.FieldType))
        {
            if (string.IsNullOrWhiteSpace(requirement.ValueSetSource))
            {
                return "Picklist value source has not been confirmed yet.";
            }

            if (requirement.ValueSetSource.Equals("global", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(requirement.GlobalValueSetName))
            {
                return "Global value set has not been confirmed yet.";
            }
        }

        if (FieldMetadataCatalog.RequiresRelationshipTarget(requirement.FieldType)
            && string.IsNullOrWhiteSpace(requirement.RelationshipTargetObject))
        {
            return "Relationship target object has not been confirmed yet.";
        }

        var path = _toolkit.GetFieldPath(repoPath, requirement);
        if (requirement.Operation.Equals("update", StringComparison.OrdinalIgnoreCase) && !File.Exists(path))
        {
            return "Existing field metadata file was not found for update.";
        }

        var existingType = _toolkit.GetExistingFieldType(path);
        if (existingType is not null && !FieldMetadataCatalog.IsCompatibleFieldUpdate(existingType, requirement.FieldType))
        {
            return $"Unsafe field type conversion from '{existingType}' to '{requirement.FieldType}' is not supported.";
        }

        return PermissionToolingCatalog.UnsupportedRequirementMessage;
    }

    public async Task<FileChangeSet?> BuildChangeSetAsync(string repoPath, SalesforceConfigRequirement requirement)
    {
        if (!CanHandle(repoPath, requirement))
        {
            return null;
        }

        var path = _toolkit.GetFieldPath(repoPath, requirement);
        var existingContent = File.Exists(path) ? await File.ReadAllTextAsync(path) : string.Empty;
        var proposedContent = _toolkit.BuildFieldContent(repoPath, requirement, existingContent);

        var proposals = new List<FileChangeProposal>
        {
            new(Path.GetRelativePath(repoPath, path), existingContent, proposedContent, File.Exists(path))
        };
        proposals.AddRange(_toolkit.BuildRecordTypeProposals(repoPath, requirement));

        return new FileChangeSet($"Field metadata updates for {requirement.Id}", proposals);
    }
}
