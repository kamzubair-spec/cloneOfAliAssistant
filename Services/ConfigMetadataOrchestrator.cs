using eZBERP_AI_IDE.Models;

using System.Text.RegularExpressions;

namespace eZBERP_AI_IDE.Services;

public sealed class ConfigMetadataOrchestrator
{
    private readonly IReadOnlyList<IConfigWorkItemHandler> _handlers;
    private readonly DeepSeekClient _deepSeekClient;
    private readonly Action<string>? _progress;

    public ConfigMetadataOrchestrator(IEnumerable<IConfigWorkItemHandler> handlers, DeepSeekClient deepSeekClient, Action<string>? progress = null)
    {
        _handlers = handlers.ToList();
        _deepSeekClient = deepSeekClient;
        _progress = progress;
    }

    public SalesforceConfigCoverage AssessCoverage(SalesforceConfigPlan plan)
    {
        return new CoverageAssessmentService(_handlers, _deepSeekClient).Assess(plan);
    }

    public SalesforceConfigCoverage AssessCoverage(string repoPath, SalesforceConfigPlan plan)
    {
        return new CoverageAssessmentService(_handlers, _deepSeekClient).Assess(repoPath, plan);
    }

    public Task<SalesforceConfigCoverage> AssessCoverageAsync(SalesforceConfigPlan plan)
    {
        return new CoverageAssessmentService(_handlers, _deepSeekClient).AssessAsync(plan);
    }

    public Task<SalesforceConfigCoverage> AssessCoverageAsync(string repoPath, SalesforceConfigPlan plan)
    {
        return new CoverageAssessmentService(_handlers, _deepSeekClient).AssessAsync(repoPath, plan);
    }

    public SalesforceConfigPlan NormalizePlan(SalesforceConfigPlan plan)
    {
        var normalized = new SalesforceConfigPlan
        {
            Summary = plan.Summary,
            Questions = plan.Questions
        };

        foreach (var requirement in plan.Requirements)
        {
            NormalizeRequirement(requirement);
            normalized.Requirements.Add(requirement);

            if (HasProfileAccess(requirement) && !requirement.Type.Equals("profile_fls_update", StringComparison.OrdinalIgnoreCase) && !requirement.Type.Equals("permission_set_fls_update", StringComparison.OrdinalIgnoreCase))
            {
                var isPermissionSetRequest = requirement.PermissionSetNames.Count > 0;
                normalized.Requirements.Add(new SalesforceConfigRequirement
                {
                    Id = $"{requirement.Id}-FLS",
                    Type = isPermissionSetRequest ? "permission_set_fls_update" : "profile_fls_update",
                    Service = isPermissionSetRequest ? nameof(PermissionSetManagementService) : nameof(ProfileManagementService),
                    Operation = "upsert",
                    ObjectApiName = requirement.ObjectApiName,
                    FieldApiName = requirement.FieldApiName,
                    ProfileAccess = requirement.ProfileAccess,
                    PermissionSetNames = requirement.PermissionSetNames
                });

                requirement.ProfileAccess = null;
            }
        }

        return normalized;
    }

    public async Task<FileChangeSet> BuildChangeSetAsync(string repoPath, SalesforceConfigPlan plan)
    {
        var normalized = NormalizePlan(plan);
        var proposals = new List<FileChangeProposal>();
        var messages = new List<string>();

        Report($"Preparing change set for {normalized.Requirements.Count} requirement(s)...");
        foreach (var requirement in normalized.Requirements)
        {
            Report($"Resolving handler for {BuildRequirementName(requirement)}...");
            var handler = _handlers.FirstOrDefault(item => CanHandlerProcess(item, repoPath, requirement));
            if (handler is null)
            {
                throw new NotSupportedException($"No config handler is available yet for requirement type '{requirement.Type}'.");
            }

            Report($"{handler.ServiceName} is building changes for {BuildRequirementName(requirement)}...");
            var changeSet = await handler.BuildChangeSetAsync(repoPath, requirement);
            if (changeSet is not null)
            {
                MergeFileProposals(proposals, changeSet.Files);
                Report($"{handler.ServiceName} proposed {changeSet.Files.Count} file change(s).");
                if (changeSet.Messages != null)
                {
                    messages.AddRange(changeSet.Messages);
                }
                
                if (changeSet.Files.Count == 0 && !string.IsNullOrWhiteSpace(changeSet.Title))
                {
                    messages.Add(changeSet.Title);
                }
            }
        }

        var finalMessages = messages.Distinct().ToList();
        return new FileChangeSet("Salesforce config metadata changes", proposals, finalMessages);
    }

    private static void MergeFileProposals(List<FileChangeProposal> proposals, IReadOnlyList<FileChangeProposal> incoming)
    {
        foreach (var proposal in incoming)
        {
            var existingIndex = proposals.FindIndex(item => item.RelativePath.Equals(proposal.RelativePath, StringComparison.OrdinalIgnoreCase));
            if (existingIndex < 0)
            {
                proposals.Add(proposal);
                continue;
            }

            var existing = proposals[existingIndex];
            if (TryMergeChangedFieldPermissions(existing.ProposedContent, proposal, out var mergedContent))
            {
                proposals[existingIndex] = existing with { ProposedContent = mergedContent };
                continue;
            }

            if (IsNoOpProposal(proposal))
            {
                continue;
            }

            proposals[existingIndex] = existing with { ProposedContent = proposal.ProposedContent };
        }
    }

    private static bool IsNoOpProposal(FileChangeProposal proposal)
    {
        return NormalizeXmlBlock(proposal.ExistingContent).Equals(NormalizeXmlBlock(proposal.ProposedContent), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryMergeChangedFieldPermissions(string baseContent, FileChangeProposal incoming, out string mergedContent)
    {
        mergedContent = baseContent;
        var changedBlocks = ExtractChangedFieldPermissionBlocks(incoming).ToList();
        if (changedBlocks.Count == 0)
        {
            return false;
        }

        foreach (var block in changedBlocks)
        {
            mergedContent = MergeFieldPermissionBlock(mergedContent, block);
        }

        return true;
    }

    private static IEnumerable<string> ExtractChangedFieldPermissionBlocks(FileChangeProposal proposal)
    {
        foreach (Match match in Regex.Matches(proposal.ProposedContent, @"<fieldPermissions>.*?</fieldPermissions>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var proposedBlock = match.Value;
            var fieldName = ExtractTagValue(proposedBlock, "field");
            if (string.IsNullOrWhiteSpace(fieldName))
            {
                continue;
            }

            var existingBlock = FindFieldPermissionBlock(proposal.ExistingContent, fieldName);
            if (!BlocksEqual(existingBlock, proposedBlock))
            {
                yield return proposedBlock;
            }
        }
    }

    private static string MergeFieldPermissionBlock(string content, string block)
    {
        var fieldName = ExtractTagValue(block, "field") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return content;
        }

        var existingMatches = Regex.Matches(content, @"<fieldPermissions>.*?</fieldPermissions>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        foreach (Match match in existingMatches)
        {
            var existingFieldName = ExtractTagValue(match.Value, "field");
            if (fieldName.Equals(existingFieldName, StringComparison.OrdinalIgnoreCase))
            {
                return content[..match.Index] + FormatBlockLike(block, match.Value, content, match.Index) + content[(match.Index + match.Length)..];
            }
        }

        foreach (Match match in existingMatches)
        {
            var existingFieldName = ExtractTagValue(match.Value, "field");
            if (!string.IsNullOrWhiteSpace(existingFieldName)
                && string.Compare(fieldName, existingFieldName, StringComparison.OrdinalIgnoreCase) < 0)
            {
                var lineStart = GetLineStart(content, match.Index);
                return content.Insert(lineStart, BuildInsertionBlock(content, block, match.Value, match.Index) + GetLineEnding(content));
            }
        }

        if (existingMatches.Count > 0)
        {
            var lastMatch = existingMatches[^1];
            var insertIndex = GetNextLineStart(content, lastMatch.Index + lastMatch.Length);
            return content.Insert(insertIndex, BuildInsertionBlock(content, block, lastMatch.Value, lastMatch.Index) + GetLineEnding(content));
        }

        var closingIndex = content.LastIndexOf("</PermissionSet>", StringComparison.OrdinalIgnoreCase);
        if (closingIndex < 0)
        {
            closingIndex = content.LastIndexOf("</Profile>", StringComparison.OrdinalIgnoreCase);
        }

        return closingIndex < 0
            ? content
            : content.Insert(closingIndex, block + GetLineEnding(content));
    }

    private static string? FindFieldPermissionBlock(string content, string fieldName)
    {
        foreach (Match match in Regex.Matches(content, @"<fieldPermissions>.*?</fieldPermissions>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var existingFieldName = ExtractTagValue(match.Value, "field");
            if (fieldName.Equals(existingFieldName, StringComparison.OrdinalIgnoreCase))
            {
                return match.Value;
            }
        }

        return null;
    }

    private static bool BlocksEqual(string? left, string right)
    {
        return left is not null
               && NormalizeXmlBlock(left).Equals(NormalizeXmlBlock(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeXmlBlock(string value)
    {
        return Regex.Replace(value, @"\s+", string.Empty);
    }

    private static string BuildInsertionBlock(string content, string block, string templateBlock, int templateIndex)
    {
        var formatted = FormatBlockLike(block, templateBlock, content, templateIndex);
        return IsSingleLineBlock(templateBlock) ? GetLinePrefix(content, templateIndex) + formatted : formatted;
    }

    private static string FormatBlockLike(string block, string templateBlock, string content, int index)
    {
        return IsSingleLineBlock(templateBlock)
            ? block
            : IndentBlock(block, DetectIndent(content, index));
    }

    private static string IndentBlock(string block, string indent)
    {
        var lines = block.Replace("\r\n", "\n").Split('\n');
        return string.Join(Environment.NewLine, lines.Select(line => indent + line.Trim()));
    }

    private static bool IsSingleLineBlock(string block)
    {
        return !block.Contains('\n') && !block.Contains('\r');
    }

    private static string? ExtractTagValue(string block, string tagName)
    {
        var match = Regex.Match(block, $@"<{tagName}>(.*?)</{tagName}>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string DetectIndent(string content, int index)
    {
        var lineStart = GetLineStart(content, index);
        var line = content[lineStart..Math.Min(index, content.Length)];
        return new string(line.TakeWhile(char.IsWhiteSpace).ToArray());
    }

    private static string GetLinePrefix(string content, int index)
    {
        var lineStart = GetLineStart(content, index);
        return new string(content[lineStart..index].TakeWhile(char.IsWhiteSpace).ToArray());
    }

    private static int GetLineStart(string content, int index)
    {
        var previousLineBreak = content.LastIndexOf('\n', Math.Max(0, index - 1));
        return previousLineBreak < 0 ? 0 : previousLineBreak + 1;
    }

    private static int GetNextLineStart(string content, int index)
    {
        var nextLineBreak = content.IndexOf('\n', index);
        return nextLineBreak < 0 ? index : nextLineBreak + 1;
    }

    private static string GetLineEnding(string content)
    {
        return content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    }

    private void Report(string message)
    {
        _progress?.Invoke(message);
    }

    private static string BuildRequirementName(SalesforceConfigRequirement requirement)
    {
        return string.Join(".", new[] { requirement.ObjectApiName, requirement.FieldApiName }.Where(value => !string.IsNullOrWhiteSpace(value)))
               is { Length: > 0 } name
            ? name
            : FirstNonBlank(requirement.Label, requirement.Id, requirement.Type);
    }

    private static string FirstNonBlank(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static bool CanHandlerProcess(IConfigWorkItemHandler handler, string repoPath, SalesforceConfigRequirement requirement)
    {
        if (!handler.CanHandle(requirement))
        {
            return false;
        }

        return handler is not IRepositoryAwareConfigWorkItemHandler repositoryAware
               || repositoryAware.CanHandle(repoPath, requirement);
    }

    private static bool HasProfileAccess(SalesforceConfigRequirement requirement)
    {
        return requirement.ProfileAccess is not null
               && (requirement.ProfileAccess.EditableProfiles.Count > 0
                   || requirement.ProfileAccess.ReadOnlyProfiles.Count > 0
                   || requirement.ProfileAccess.ApplyReadOnlyToRemainingProfiles);
    }

    private static void NormalizeRequirement(SalesforceConfigRequirement requirement)
    {
        NormalizePageOrActionRequirement(requirement);
        requirement.Type = NormalizeType(requirement.Type);
        requirement.Service = NormalizeService(requirement.Service, requirement.Type);
        requirement.Operation = string.IsNullOrWhiteSpace(requirement.Operation) ? "upsert" : requirement.Operation.Trim().ToLowerInvariant();
        requirement.ObjectApiName = NormalizeCustomApiName(requirement.ObjectApiName);
        requirement.CustomMetadataTypeApiName = NormalizeCustomMetadataTypeName(requirement.CustomMetadataTypeApiName);
        requirement.RecordDeveloperName = NormalizeMetadataDeveloperName(requirement.RecordDeveloperName);
        if (requirement.Type.Equals("layout", StringComparison.OrdinalIgnoreCase)
            || requirement.Type.Equals("flexipage", StringComparison.OrdinalIgnoreCase)
            || requirement.Type.Equals("quick_action", StringComparison.OrdinalIgnoreCase))
        {
            requirement.FieldApiName = NormalizeMetadataFieldName(requirement.FieldApiName);
            requirement.ExistingFieldApiName = NormalizeMetadataFieldName(requirement.ExistingFieldApiName);
            requirement.ReplaceFieldApiName = NormalizeMetadataFieldName(requirement.ReplaceFieldApiName);
        }
        else
        {
            requirement.FieldApiName = NormalizeFieldApiName(requirement.FieldApiName);
            requirement.ExistingFieldApiName = NormalizeFieldApiName(requirement.ExistingFieldApiName);
            requirement.ReplaceFieldApiName = NormalizeFieldApiName(requirement.ReplaceFieldApiName);
        }
    }

    private static void NormalizePageOrActionRequirement(SalesforceConfigRequirement requirement)
    {
        if (!requirement.Type.Equals("layout", StringComparison.OrdinalIgnoreCase)
            && !requirement.Type.Equals("flexipage", StringComparison.OrdinalIgnoreCase)
            && !requirement.Type.Equals("quick_action", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (MentionsLightningPage(requirement)
            && requirement.Type.Equals("layout", StringComparison.OrdinalIgnoreCase))
        {
            requirement.Type = "flexipage";
            requirement.Service = nameof(FlexipageManagementService);
            requirement.PreferredTargetType = "flexipage";
        }

        if (LooksLikeRelatedRecordQuickAction(requirement))
        {
            requirement.Type = "quick_action";
            requirement.Service = nameof(QuickActionManagementService);
            requirement.PreferredTargetType = "quick_action";

            if (string.IsNullOrWhiteSpace(requirement.TargetMetadataName)
                && MentionsOrganisationDetails(requirement))
            {
                requirement.TargetMetadataName = "Account.Organisation_Details";
                requirement.TargetLayoutOrPageLabel = string.IsNullOrWhiteSpace(requirement.TargetLayoutOrPageLabel)
                    ? "Organisation Details"
                    : requirement.TargetLayoutOrPageLabel;
            }
        }
    }

    private static bool MentionsLightningPage(SalesforceConfigRequirement requirement)
    {
        var text = $"{requirement.TargetMetadataName} {requirement.TargetLayoutOrPageLabel} {requirement.Label} {requirement.Description}";
        return text.Contains("revolution page", StringComparison.OrdinalIgnoreCase)
               || text.Contains("record page", StringComparison.OrdinalIgnoreCase)
               || text.Contains("lightning page", StringComparison.OrdinalIgnoreCase)
               || requirement.PreferredTargetType.Equals("flexipage", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeRelatedRecordQuickAction(SalesforceConfigRequirement requirement)
    {
        var text = $"{requirement.TargetMetadataName} {requirement.TargetLayoutOrPageLabel} {requirement.TargetSectionLabel} {requirement.TargetRegionOrComponent} {requirement.Label} {requirement.Description}";
        return MentionsOrganisationDetails(requirement)
               || text.Contains("quick action", StringComparison.OrdinalIgnoreCase)
               || text.Contains("related record", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MentionsOrganisationDetails(SalesforceConfigRequirement requirement)
    {
        var text = $"{requirement.TargetMetadataName} {requirement.TargetLayoutOrPageLabel} {requirement.TargetSectionLabel} {requirement.TargetRegionOrComponent} {requirement.Label} {requirement.Description}";
        return text.Contains("organisation details", StringComparison.OrdinalIgnoreCase)
               || text.Contains("organization details", StringComparison.OrdinalIgnoreCase);
    }
    private static string NormalizeType(string type)
    {
        var value = (type ?? string.Empty).Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        return value switch
        {
            "create_field" => "field_create",
            "update_field" => "field_update",
            "upsert_field" => "field_upsert",
            "fls" or "field_level_security" => "profile_fls_update",
            _ => value
        };
    }

    private static string NormalizeService(string service, string type)
    {
        if (!string.IsNullOrWhiteSpace(service))
        {
            return service.Trim();
        }

        return type switch
        {
            "field_create" or "field_update" or "field_upsert" or "picklist" or "picklist_value" or "picklist_value_add" or "validation_rule" or "validation_rule_create" => nameof(ObjectManagementService),
            "profile_fls_update" => nameof(ProfileManagementService),
            "permission_set" or "permission_set_fls_update" => nameof(PermissionSetManagementService),
            "record_type" => nameof(RecordTypeManagementService),
            "custom_label" => nameof(LabelManagementService),
            "custom_metadata" => nameof(CustomMetadataManagementService),
            "custom_permission" => nameof(CustomPermissionManagementService),
            "global_value_set" => nameof(GlobalValueSetManagementService),
            "layout" => nameof(LayoutManagementService),
            "flexipage" => nameof(FlexipageManagementService),
            "quick_action" => nameof(QuickActionManagementService),
            _ => string.Empty
        };
    }

    private static string NormalizeCustomMetadataTypeName(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return string.Empty;
        }

        var value = typeName.Trim();
        return value.EndsWith("__mdt", StringComparison.OrdinalIgnoreCase) ? value[..^5] : value;
    }

    private static string NormalizeMetadataDeveloperName(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : System.Text.RegularExpressions.Regex.Replace(value.Trim(), @"[^A-Za-z0-9_]+", "_").Trim('_');
    }

    private static string NormalizeMetadataFieldName(string fieldApiName)
    {
        if (string.IsNullOrWhiteSpace(fieldApiName))
        {
            return string.Empty;
        }

        var value = fieldApiName.Trim()
            .Replace("{!Record.", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Record.", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim('}', ' ');

        return HasCustomFieldSuffix(value)
            ? NormalizeCustomApiName(value)
            : value;
    }
    private static string NormalizeFieldApiName(string fieldApiName)
    {
        if (string.IsNullOrWhiteSpace(fieldApiName))
        {
            return string.Empty;
        }

        var value = fieldApiName.Trim();
        if (value.StartsWith("Field ", StringComparison.OrdinalIgnoreCase))
        {
            value = value["Field ".Length..].Trim();
        }

        if (string.IsNullOrWhiteSpace(value) || value.Equals("Field", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (HasCustomFieldSuffix(value))
        {
            return NormalizeCustomApiName(value);
        }

        return NormalizeCustomApiName(value.TrimEnd('_') + "__c");
    }

    private static string NormalizeCustomApiName(string apiName)
    {
        if (string.IsNullOrWhiteSpace(apiName))
        {
            return string.Empty;
        }

        var value = apiName.Trim();
        var suffix = GetCustomFieldSuffix(value);
        if (string.IsNullOrWhiteSpace(suffix))
        {
            return value;
        }

        var baseName = value[..^suffix.Length];
        var parts = baseName
            .Split(new[] { '_', ' ', '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]);

        return string.Join("_", parts) + suffix.ToLowerInvariant();
    }

    private static bool HasCustomFieldSuffix(string value)
    {
        return !string.IsNullOrWhiteSpace(GetCustomFieldSuffix(value));
    }

    private static string GetCustomFieldSuffix(string value)
    {
        if (value.EndsWith("__pc", StringComparison.OrdinalIgnoreCase))
        {
            return "__pc";
        }

        return value.EndsWith("__c", StringComparison.OrdinalIgnoreCase) ? "__c" : string.Empty;
    }
}






