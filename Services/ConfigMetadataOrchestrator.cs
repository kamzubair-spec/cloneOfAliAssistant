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

    public SalesforceConfigPlan NormalizePlan(SalesforceConfigPlan plan)
    {
        var normalized = new SalesforceConfigPlan
        {
            Summary = plan.Summary,
            Questions = plan.Questions
        };

        foreach (var requirement in plan.Requirements)
        {
            requirement.Type = NormalizeType(requirement.Type);
            requirement.Service = NormalizeService(requirement.Service, requirement.Type);
            requirement.Operation = string.IsNullOrWhiteSpace(requirement.Operation) ? "update" : requirement.Operation.Trim().ToLowerInvariant();
            normalized.Requirements.Add(requirement);
        }

        return normalized;
    }

    public Task<SalesforceConfigCoverage> AssessCoverageAsync(string repoPath, SalesforceConfigPlan plan)
    {
        return new CoverageAssessmentService(_handlers, _deepSeekClient).AssessAsync(repoPath, plan);
    }

    public async Task<FileChangeSet> BuildChangeSetAsync(string repoPath, SalesforceConfigPlan plan)
    {
        var normalized = NormalizePlan(plan);
        var proposals = new List<FileChangeProposal>();
        var messages = new List<string>();

        foreach (var requirement in normalized.Requirements)
        {
            var handler = _handlers.FirstOrDefault(item => item.CanHandle(requirement));
            if (handler is null) continue;

            var changeSet = await handler.BuildChangeSetAsync(repoPath, requirement);
            if (changeSet != null)
            {
                MergeFileProposals(proposals, changeSet.Files);
                if (changeSet.Messages != null) messages.AddRange(changeSet.Messages);
            }
        }

        return new FileChangeSet("Salesforce config metadata changes", proposals, messages.Distinct().ToList());
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
            proposals[existingIndex] = existing with { ProposedContent = MergeContent(existing.ProposedContent, proposal.ProposedContent) };
        }
    }

    private static string MergeContent(string baseContent, string incomingContent)
    {
        if (baseContent == incomingContent) return baseContent;
        
        var tags = new[] { "fieldPermissions", "tabVisibilities", "classAccesses", "objectPermissions", "customPermissions", "pageAccesses", "recordTypeVisibilities", "applicationVisibilities", "userPermissions" };
        var merged = baseContent;

        foreach (var tag in tags)
        {
            var pattern = $@"<{tag}>.*?</{tag}>";
            var incomingMatches = Regex.Matches(incomingContent, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
            
            foreach (Match match in incomingMatches)
            {
                if (!merged.Contains(match.Value, StringComparison.OrdinalIgnoreCase))
                {
                    merged = MergeSingleBlock(merged, match.Value, tag);
                }
            }
        }
        return merged;
    }

    private static string MergeSingleBlock(string content, string block, string tag)
    {
        var keyTag = tag switch {
            "fieldPermissions" => "field",
            "tabVisibilities" => "tab",
            "classAccesses" => "apexClass",
            "objectPermissions" => "object",
            "customPermissions" => "name",
            "pageAccesses" => "apexPage",
            "recordTypeVisibilities" => "recordType",
            "applicationVisibilities" => "application",
            "userPermissions" => "name",
            _ => null
        };

        if (keyTag == null) return content;

        var keyMatch = Regex.Match(block, $@"<{keyTag}>(.*?)</{keyTag}>", RegexOptions.IgnoreCase);
        if (!keyMatch.Success) return content;
        var key = keyMatch.Groups[1].Value;

        var existingPattern = $@"<{tag}><{keyTag}>{Regex.Escape(key)}</{keyTag}>.*?</{tag}>";
        if (Regex.IsMatch(content, existingPattern, RegexOptions.IgnoreCase))
        {
            return Regex.Replace(content, existingPattern, block, RegexOptions.IgnoreCase);
        }

        var rootTag = content.Contains("</PermissionSet>") ? "</PermissionSet>" : "</Profile>";
        var index = content.LastIndexOf(rootTag);
        return index < 0 ? content : content.Insert(index, block + "\n");
    }

    private static string NormalizeType(string type)
    {
        var value = (type ?? string.Empty).Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        var normalized = value switch
        {
            "fls" or "field_level_security" => "profile_fls_update",
            "profile" => "profile_metadata",
            "permissionset" => "permission_set",
            "custompermission" => "custom_permission",
            _ => value
        };

        return PermissionToolingCatalog.IsSupportedRequirementType(normalized)
            ? normalized
            : "unsupported_requirement";
    }

    private static string NormalizeService(string service, string type)
    {
        return type switch
        {
            "profile_metadata" or "profile_fls_update" or "permission_set" or "permission_set_fls_update" or "custom_permission" => nameof(PermissionManagementService),
            _ => string.Empty
        };
    }
}
