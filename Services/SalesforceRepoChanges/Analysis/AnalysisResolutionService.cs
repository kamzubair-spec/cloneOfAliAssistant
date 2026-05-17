using eZBERP_AI_IDE.Models;

namespace eZBERP_AI_IDE.Services;

public sealed class AnalysisResolutionService
{
    private const int AutoResolveHighConfidenceThreshold = 90;
    private const int AutoResolveGapThreshold = 15;
    private readonly MetadataDiscoveryService _metadataDiscoveryService;
    private readonly Dictionary<string, string> _storyLevelResolvedObjects = new(StringComparer.OrdinalIgnoreCase);

    public AnalysisResolutionService(MetadataDiscoveryService metadataDiscoveryService)
    {
        _metadataDiscoveryService = metadataDiscoveryService;
    }

    public async Task<SalesforceConfigPlan> ResolvePlanAsync(string repoPath, SalesforceConfigPlan plan, Func<ResolutionPrompt, Task<ResolutionResponse?>> promptUserAsync, string sharedContextText = "")
    {
        _storyLevelResolvedObjects.Clear();

        foreach (var requirement in plan.Requirements)
        {
            requirement.ResolutionOptions.Clear();
            requirement.NeedsUserConfirmation = false;
            requirement.AmbiguityReason = string.Empty;
            await ResolveRequirementAsync(repoPath, requirement, promptUserAsync, sharedContextText);
        }

        return plan;
    }

    private async Task ResolveRequirementAsync(string repoPath, SalesforceConfigRequirement requirement, Func<ResolutionPrompt, Task<ResolutionResponse?>> promptUserAsync, string sharedContextText)
    {
        if (FieldMetadataCatalog.IsSupportedRequirementType(requirement.Type))
        {
            await ResolveFieldRequirementAsync(repoPath, requirement, promptUserAsync, sharedContextText);
            return;
        }

        if (PermissionToolingCatalog.IsProfileRequirement(requirement.Type) || PermissionToolingCatalog.IsPermissionSetRequirement(requirement.Type))
        {
            await ResolveAudienceRequirementAsync(repoPath, requirement, promptUserAsync);
        }
    }

    private async Task ResolveFieldRequirementAsync(string repoPath, SalesforceConfigRequirement requirement, Func<ResolutionPrompt, Task<ResolutionResponse?>> promptUserAsync, string sharedContextText)
    {
        var storyLevelObjectKey = BuildStoryLevelObjectKey(sharedContextText);
        if (string.IsNullOrWhiteSpace(requirement.ObjectApiName)
            && !string.IsNullOrWhiteSpace(storyLevelObjectKey)
            && _storyLevelResolvedObjects.TryGetValue(storyLevelObjectKey, out var storyLevelObject))
        {
            requirement.ObjectApiName = storyLevelObject;
        }

        if (string.IsNullOrWhiteSpace(requirement.ObjectApiName) || !_metadataDiscoveryService.ObjectExists(repoPath, requirement.ObjectApiName))
        {
            var searchText = FirstNonBlank(requirement.ObjectApiName, requirement.Label, requirement.Description, requirement.FieldDescription, sharedContextText);
            var objectCandidates = _metadataDiscoveryService.FindObjectCandidates(repoPath, searchText);
            requirement.ResolutionOptions.AddRange(objectCandidates);
            if (ShouldAutoResolve(objectCandidates, out var autoResolvedObject))
            {
                requirement.ObjectApiName = autoResolvedObject!.Id;
            }
            else if (objectCandidates.Count > 1)
            {
                var response = await promptUserAsync(new ResolutionPrompt
                {
                    RequirementId = requirement.Id,
                    Prompt = $"Select the target object for {FirstNonBlank(requirement.FieldApiName, requirement.Label, "this field request")}.",
                    AllowMultiple = false,
                    Kind = "object",
                    Options = objectCandidates
                });

                if (response?.SelectedOptionIds.Count > 0)
                {
                    requirement.ObjectApiName = response.SelectedOptionIds[0];
                }
                else
                {
                    requirement.NeedsUserConfirmation = true;
                    requirement.AmbiguityReason = "Object selection was not confirmed.";
                }
            }
            else if (objectCandidates.Count == 1)
            {
                var response = await promptUserAsync(new ResolutionPrompt
                {
                    RequirementId = requirement.Id,
                    Prompt = $"Confirm the target object for {FirstNonBlank(requirement.FieldApiName, requirement.Label, "this field request")}.",
                    AllowMultiple = false,
                    Kind = "object",
                    Options = objectCandidates
                });

                if (response?.SelectedOptionIds.Count > 0)
                {
                    requirement.ObjectApiName = response.SelectedOptionIds[0];
                }
                else
                {
                    requirement.NeedsUserConfirmation = true;
                    requirement.AmbiguityReason = "Object selection was not confirmed.";
                }
            }
        }

        if (string.IsNullOrWhiteSpace(requirement.ObjectApiName))
        {
            requirement.NeedsUserConfirmation = true;
            requirement.AmbiguityReason = "Object could not be resolved.";
            requirement.IsResolved = false;
            return;
        }

        if (!string.IsNullOrWhiteSpace(storyLevelObjectKey))
        {
            _storyLevelResolvedObjects[storyLevelObjectKey] = requirement.ObjectApiName;
        }

        if (IsPicklistRequirement(requirement))
        {
            await ResolvePicklistRequirementAsync(repoPath, requirement, promptUserAsync);
        }

        if (IsRelationshipRequirement(requirement) && string.IsNullOrWhiteSpace(requirement.RelationshipTargetObject))
        {
            var objectCandidates = _metadataDiscoveryService.FindObjectCandidates(repoPath, FirstNonBlank(requirement.Description, requirement.Label));
            requirement.ResolutionOptions.AddRange(objectCandidates);
            if (ShouldAutoResolve(objectCandidates, out var autoResolvedTarget))
            {
                requirement.RelationshipTargetObject = autoResolvedTarget!.Id;
            }
            else if (objectCandidates.Count > 0)
            {
                var response = await promptUserAsync(new ResolutionPrompt
                {
                    RequirementId = requirement.Id,
                    Prompt = $"Select the related object for {FirstNonBlank(requirement.FieldApiName, requirement.Label, "this relationship field")}.",
                    AllowMultiple = false,
                    Kind = "relationship-object",
                    Options = objectCandidates
                });

                if (response?.SelectedOptionIds.Count > 0)
                {
                    requirement.RelationshipTargetObject = response.SelectedOptionIds[0];
                }
                else
                {
                    requirement.NeedsUserConfirmation = true;
                    requirement.AmbiguityReason = "Relationship target object was not confirmed.";
                }
            }
            else if (objectCandidates.Count == 1)
            {
                var response = await promptUserAsync(new ResolutionPrompt
                {
                    RequirementId = requirement.Id,
                    Prompt = $"Confirm the related object for {FirstNonBlank(requirement.FieldApiName, requirement.Label, "this relationship field")}.",
                    AllowMultiple = false,
                    Kind = "relationship-object",
                    Options = objectCandidates
                });

                if (response?.SelectedOptionIds.Count > 0)
                {
                    requirement.RelationshipTargetObject = response.SelectedOptionIds[0];
                }
                else
                {
                    requirement.NeedsUserConfirmation = true;
                    requirement.AmbiguityReason = "Relationship target object was not confirmed.";
                }
            }
        }

        requirement.IsResolved = !requirement.NeedsUserConfirmation
            && !string.IsNullOrWhiteSpace(requirement.ObjectApiName)
            && (!IsRelationshipRequirement(requirement) || !string.IsNullOrWhiteSpace(requirement.RelationshipTargetObject))
            && (!requirement.ValueSetSource.Equals("global", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(requirement.GlobalValueSetName))
            && (!RequiresControllingField(requirement) || !string.IsNullOrWhiteSpace(requirement.ControllingFieldApiName));
    }

    private async Task ResolvePicklistRequirementAsync(string repoPath, SalesforceConfigRequirement requirement, Func<ResolutionPrompt, Task<ResolutionResponse?>> promptUserAsync)
    {
        var objectRecordTypes = _metadataDiscoveryService.GetRecordTypeNames(repoPath, requirement.ObjectApiName);

        if (string.IsNullOrWhiteSpace(requirement.ValueSetSource))
        {
            var response = await promptUserAsync(new ResolutionPrompt
            {
                RequirementId = requirement.Id,
                Prompt = $"Should {FirstNonBlank(requirement.FieldApiName, requirement.Label, "this picklist field")} use local values or a global value set?",
                AllowMultiple = false,
                Kind = "value-set-source",
                Options = new List<ResolutionOption>
                {
                    new() { Id = "local", Label = "Local values", Type = "Value Source" },
                    new() { Id = "global", Label = "Global value set", Type = "Value Source" }
                }
            });

            if (response?.SelectedOptionIds.Count > 0)
            {
                requirement.ValueSetSource = response.SelectedOptionIds[0];
            }
            else
            {
                requirement.NeedsUserConfirmation = true;
                requirement.AmbiguityReason = "Picklist value source was not confirmed.";
            }
        }

        if (requirement.ValueSetSource.Equals("global", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(requirement.GlobalValueSetName))
        {
            var candidates = _metadataDiscoveryService.FindGlobalValueSetCandidates(repoPath, FirstNonBlank(requirement.GlobalValueSetName, requirement.Label, requirement.Description));
            requirement.ResolutionOptions.AddRange(candidates);
            if (candidates.Count > 0)
            {
                var response = await promptUserAsync(new ResolutionPrompt
                {
                    RequirementId = requirement.Id,
                    Prompt = $"Select the global value set for {FirstNonBlank(requirement.FieldApiName, requirement.Label, "this picklist field")}.",
                    AllowMultiple = false,
                    Kind = "global-value-set",
                    Options = candidates
                });

                if (response?.SelectedOptionIds.Count > 0)
                {
                    requirement.GlobalValueSetName = response.SelectedOptionIds[0];
                }
                else
                {
                    requirement.NeedsUserConfirmation = true;
                    requirement.AmbiguityReason = "Global value set selection was not confirmed.";
                }
            }
            else
            {
                requirement.NeedsUserConfirmation = true;
                requirement.AmbiguityReason = "A global value set was required but no candidates were found.";
            }
        }

        var likelyDependent = RequiresControllingField(requirement);

        if (likelyDependent && string.IsNullOrWhiteSpace(requirement.ControllingFieldApiName))
        {
            var fieldCandidates = _metadataDiscoveryService.GetFieldNames(repoPath, requirement.ObjectApiName)
                .Select(name => new ResolutionOption { Id = name, Label = name, Type = "Field" })
                .ToList();
            requirement.ResolutionOptions.AddRange(fieldCandidates);

            var response = await promptUserAsync(new ResolutionPrompt
            {
                RequirementId = requirement.Id,
                Prompt = $"Select the controlling field for {FirstNonBlank(requirement.FieldApiName, requirement.Label, "this dependent picklist")}.",
                AllowMultiple = false,
                Kind = "controlling-field",
                Options = fieldCandidates
            });

            if (response?.SelectedOptionIds.Count > 0)
            {
                requirement.ControllingFieldApiName = response.SelectedOptionIds[0];
            }
            else
            {
                requirement.NeedsUserConfirmation = true;
                requirement.AmbiguityReason = "Controlling field selection was not confirmed.";
            }
        }

        if (objectRecordTypes.Count > 1 && requirement.RecordTypeNames.Count == 0)
        {
            var response = await promptUserAsync(new ResolutionPrompt
            {
                RequirementId = requirement.Id,
                Prompt = $"Select the record types that should include values for {FirstNonBlank(requirement.FieldApiName, requirement.Label, "this picklist field")}.",
                AllowMultiple = true,
                Kind = "record-types",
                Options = objectRecordTypes.Select(name => new ResolutionOption { Id = name, Label = name, Type = "Record Type" }).ToList()
            });

            if (response?.SelectedOptionIds.Count > 0)
            {
                requirement.RecordTypeNames = response.SelectedOptionIds;
            }
            else
            {
                requirement.NeedsUserConfirmation = true;
                requirement.AmbiguityReason = "Record type selection was not confirmed.";
            }
        }
    }

    private async Task ResolveAudienceRequirementAsync(string repoPath, SalesforceConfigRequirement requirement, Func<ResolutionPrompt, Task<ResolutionResponse?>> promptUserAsync)
    {
        var audience = FirstNonBlank(requirement.AudienceName, requirement.TargetMetadataName, requirement.Label, requirement.Description);
        if (string.IsNullOrWhiteSpace(audience))
        {
            return;
        }

        var profileCandidates = _metadataDiscoveryService.FindProfileCandidates(repoPath, audience);
        var permissionSetCandidates = _metadataDiscoveryService.FindPermissionSetCandidates(repoPath, audience);
        var combined = profileCandidates.Concat(permissionSetCandidates).ToList();
        requirement.ResolutionOptions.AddRange(combined);
        if (combined.Count == 0)
        {
            requirement.NeedsUserConfirmation = true;
            requirement.AmbiguityReason = $"No matching profile or permission set candidates were found for '{audience}'.";
            return;
        }

        if (ShouldAutoResolve(combined, out var autoResolvedAudience))
        {
            ApplyAudienceSelection(requirement, autoResolvedAudience!);
            return;
        }

        if (combined.Count == 1)
        {
            var singleCandidateResponse = await promptUserAsync(new ResolutionPrompt
            {
                RequirementId = requirement.Id,
                Prompt = $"Confirm the profile or permission set target for '{audience}'.",
                AllowMultiple = true,
                Kind = "audience",
                Options = combined
            });

            if (singleCandidateResponse?.SelectedOptionIds.Count > 0)
            {
                foreach (var option in combined.Where(option => singleCandidateResponse.SelectedOptionIds.Contains(option.Id, StringComparer.OrdinalIgnoreCase)))
                {
                    ApplyAudienceSelection(requirement, option);
                }

                return;
            }

            requirement.NeedsUserConfirmation = true;
            requirement.AmbiguityReason = $"Audience selection for '{audience}' was not confirmed.";
            return;
        }

        var response = await promptUserAsync(new ResolutionPrompt
        {
            RequirementId = requirement.Id,
            Prompt = $"Select the profile and/or permission set targets for '{audience}'.",
            AllowMultiple = true,
            Kind = "audience",
            Options = combined
        });

        if (response?.SelectedOptionIds.Count > 0)
        {
            foreach (var option in combined.Where(option => response.SelectedOptionIds.Contains(option.Id, StringComparer.OrdinalIgnoreCase)))
            {
                ApplyAudienceSelection(requirement, option);
            }
        }
        else
        {
            requirement.NeedsUserConfirmation = true;
            requirement.AmbiguityReason = $"Audience selection for '{audience}' was not confirmed.";
        }
    }

    private static void ApplyAudienceSelection(SalesforceConfigRequirement requirement, ResolutionOption option)
    {
        if (option.Type.Equals("Profile", StringComparison.OrdinalIgnoreCase))
        {
            requirement.ProfileAccess ??= new ProfileAccessRequirement();
            if (!requirement.ProfileAccess.EditableProfiles.Contains(option.Id, StringComparer.OrdinalIgnoreCase)
                && !requirement.ProfileAccess.ReadOnlyProfiles.Contains(option.Id, StringComparer.OrdinalIgnoreCase))
            {
                requirement.ProfileAccess.EditableProfiles.Add(option.Id);
            }
        }
        else if (option.Type.Equals("Permission Set", StringComparison.OrdinalIgnoreCase))
        {
            if (!requirement.PermissionSetNames.Contains(option.Id, StringComparer.OrdinalIgnoreCase))
            {
                requirement.PermissionSetNames.Add(option.Id);
            }
        }
    }

    private static bool IsPicklistRequirement(SalesforceConfigRequirement requirement)
    {
        var fieldType = FieldMetadataCatalog.NormalizeFieldType(requirement.FieldType);
        return fieldType is "picklist" or "multiselectpicklist";
    }

    private static bool IsRelationshipRequirement(SalesforceConfigRequirement requirement)
    {
        var fieldType = FieldMetadataCatalog.NormalizeFieldType(requirement.FieldType);
        return fieldType is "lookup" or "masterdetail";
    }

    private static bool RequiresControllingField(SalesforceConfigRequirement requirement)
    {
        return requirement.PicklistEntries.Any(entry => entry.ControllingValues.Count > 0)
            || requirement.Description.Contains("dependent", StringComparison.OrdinalIgnoreCase)
            || requirement.FieldDescription.Contains("dependent", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldAutoResolve(List<ResolutionOption> candidates, out ResolutionOption? selectedOption)
    {
        selectedOption = null;
        if (candidates.Count == 0)
        {
            return false;
        }

        if (candidates.Count == 1)
        {
            if (candidates[0].ConfidenceScore >= AutoResolveHighConfidenceThreshold)
            {
                selectedOption = candidates[0];
                return true;
            }

            return false;
        }

        var ordered = candidates
            .OrderByDescending(candidate => candidate.ConfidenceScore)
            .ThenBy(candidate => candidate.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var top = ordered[0];
        var second = ordered[1];
        if (top.ConfidenceScore >= AutoResolveHighConfidenceThreshold
            && top.ConfidenceScore - second.ConfidenceScore >= AutoResolveGapThreshold)
        {
            selectedOption = top;
            return true;
        }

        return false;
    }

    private static string FirstNonBlank(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static string BuildStoryLevelObjectKey(string sharedContextText)
    {
        return string.IsNullOrWhiteSpace(sharedContextText)
            ? string.Empty
            : sharedContextText.Trim();
    }
}

public sealed class ResolutionPrompt
{
    public string RequirementId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public bool AllowMultiple { get; set; }
    public List<ResolutionOption> Options { get; set; } = new();
}

public sealed class ResolutionResponse
{
    public List<string> SelectedOptionIds { get; set; } = new();
}
