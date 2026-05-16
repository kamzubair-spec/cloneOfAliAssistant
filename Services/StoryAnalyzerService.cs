using System.Text.Json;
using System.Text.RegularExpressions;
using eZBERP_AI_IDE.Models;
using File = System.IO.File;

namespace eZBERP_AI_IDE.Services;

public sealed class StoryAnalyzerService
{
    private readonly DeepSeekClient _deepSeekClient;

    public StoryAnalyzerService(DeepSeekClient deepSeekClient)
    {
        _deepSeekClient = deepSeekClient;
    }

    public bool IsSalesforceConfigRequest(string userCommand)
    {
        var configKeywords = new[]
        {
            "field", "fields", "fls", "field level security", "profile", "permission set", "permission",
            "object", "validation rule", "record type", "picklist", "tooltip", "inline help", "formula",
            "page layout", "layout", "flexipage", "quick action", "flow", "custom metadata",
            "custom permission", "custom label", "label", "global value set", "globalvalueset", "standard value set"
        };

        if (configKeywords.Any(keyword => userCommand.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var implementationKeywords = new[] { "lwc", "apex", "trigger", "aura", "visualforce", "javascript" };
        return implementationKeywords.Any(keyword => userCommand.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<SalesforceConfigPlan> AnalyzeAsync(string repoPath, string userCommand)
    {
        var systemPrompt = BuildSystemPrompt(repoPath);
        var response = await _deepSeekClient.SendChatAsync(DeepSeekModels.Config, systemPrompt, userCommand, 0.0, 6000);
        var plan = await ParsePlanResponseAsync(systemPrompt, userCommand, response);
        NormalizeIntegrationUserAccessTargets(plan, repoPath);
        return plan;
    }

    public async Task<SalesforceConfigPlan> AnalyzeAsync(string repoPath, JiraStoryAnalysisContent storyContent)
    {
        if (storyContent.HasInlineImages && AiProviderSettings.UseOpenAiForInlineImages)
        {
            var systemPrompt = BuildSystemPrompt(repoPath);
            var userContent = BuildVisionContent(storyContent);
            var response = await _deepSeekClient.SendVisionChatAsync(DeepSeekModels.Vision, systemPrompt, userContent, 0.0, 6000);
            var plan = await ParsePlanResponseAsync(systemPrompt, storyContent.PlainText, response);
            NormalizeIntegrationUserAccessTargets(plan, repoPath);
            return plan;
        }

        return await AnalyzeAsync(repoPath, storyContent.PlainText);
    }

    public async Task<string> DescribeInlineImagesAsync(JiraStoryAnalysisContent storyContent)
    {
        if (!storyContent.HasInlineImages)
        {
            return "No inline image blocks were found in the Jira story analysis payload.";
        }

        if (!AiProviderSettings.UseOpenAiForInlineImages)
        {
            return "Inline images were found, but OpenAI vision routing is disabled. Set EZBERP_USE_OPENAI_FOR_INLINE_IMAGES=true to test image reading.";
        }

        var systemPrompt = """
You are testing Jira screenshot reading for a Salesforce delivery assistant.
Read the inline images carefully and report only what you can actually see.
Do not infer missing field names.
Return a concise plain-text diagnostic with:
1. Image count seen.
2. For each image, the visible Salesforce page/layout/section names.
3. Visible field labels or API names, especially highlighted fields.
4. Any field replacement, visibility, or layout instructions visible in the screenshot.
5. Any uncertainty or unreadable areas.
""";

        var userContent = BuildVisionContent(storyContent)
            .Concat(new[]
            {
                new AiChatContentPart(
                    AiChatContentKind.Text,
                    "Diagnostic request: What did you read from the inline Jira images? Focus on field names, highlighted fields, page names, section names, and layout/flexipage instructions.")
            })
            .ToList();

        return await _deepSeekClient.SendVisionChatAsync(DeepSeekModels.Vision, systemPrompt, userContent, 0.0, 3000);
    }

    private async Task<SalesforceConfigPlan> ParsePlanResponseAsync(string systemPrompt, string userCommand, string response)
    {
        var json = await ExtractJsonOrRepairAsync(systemPrompt, userCommand, response);

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        var plan = await DeserializePlanOrRepairAsync(systemPrompt, userCommand, json, options);

        EnsurePlanDefaults(plan);
        NormalizeCandidatePersonAccountRequirements(plan);
        NormalizeRemainingProfileReadOnlyAccess(plan);
        NormalizeMisclassifiedFlowRequirements(plan);
        SplitCombinedQuickActionConditionalRequirements(plan);
        RemoveAcceptanceCriteriaFalsePositives(plan);
        return plan;
    }

    private static IReadOnlyList<AiChatContentPart> BuildVisionContent(JiraStoryAnalysisContent storyContent)
    {
        var content = new List<AiChatContentPart>();
        foreach (var block in storyContent.Blocks)
        {
            if (block.Kind.Equals("image", StringComparison.OrdinalIgnoreCase))
            {
                var dataUrl = TryBuildImageDataUrl(block.LocalPath, block.MimeType);
                if (string.IsNullOrWhiteSpace(dataUrl))
                {
                    content.Add(new AiChatContentPart(AiChatContentKind.Text, $"[Inline image could not be loaded: {block.FileName}]"));
                    continue;
                }

                content.Add(new AiChatContentPart(
                    AiChatContentKind.Text,
                    $"Inline Jira image follows here: {block.FileName}. Analyze it in the context of the surrounding story text."));
                content.Add(new AiChatContentPart(AiChatContentKind.Image, FileName: block.FileName, MimeType: block.MimeType, DataUrl: dataUrl));
            }
            else if (!string.IsNullOrWhiteSpace(block.Text))
            {
                content.Add(new AiChatContentPart(AiChatContentKind.Text, block.Text));
            }
        }

        if (content.Count == 0)
        {
            content.Add(new AiChatContentPart(AiChatContentKind.Text, storyContent.PlainText));
        }

        return content;
    }

    private static string TryBuildImageDataUrl(string path, string mimeType)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            const int maxImageBytes = 10_000_000;
            if (bytes.Length == 0 || bytes.Length > maxImageBytes)
            {
                return string.Empty;
            }

            var resolvedMimeType = string.IsNullOrWhiteSpace(mimeType) ? GuessMimeType(path) : mimeType;
            return $"data:{resolvedMimeType};base64,{Convert.ToBase64String(bytes)}";
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GuessMimeType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "image/png"
        };
    }

    private async Task<SalesforceConfigPlan> DeserializePlanOrRepairAsync(
        string systemPrompt,
        string userCommand,
        string json,
        JsonSerializerOptions options)
    {
        if (TryDeserializePlan(json, options, out var plan, out var parseError))
        {
            return plan;
        }

        var repairPrompt = $$"""
The previous response contained JSON-like text, but it was malformed and could not be parsed.
Repair it into the exact Salesforce config roadmap JSON schema requested by the system prompt.
Return JSON only. No markdown. No explanation.

Parse error:
{{parseError}}

Story:
{{userCommand}}

Malformed JSON:
{{json}}
""";

        var repairedResponse = await _deepSeekClient.SendChatAsync(DeepSeekModels.Config, systemPrompt, repairPrompt, 0.0, 6000);
        if (!TryExtractJson(repairedResponse, out var repairedJson))
        {
            throw new InvalidOperationException($"The model returned malformed JSON and the repair response did not contain JSON. Original parse error: {parseError}");
        }

        if (TryDeserializePlan(repairedJson, options, out plan, out var repairedParseError))
        {
            return plan;
        }

        throw new InvalidOperationException($"The model returned malformed JSON for the Salesforce config roadmap and repair also failed: {repairedParseError}");
    }

    private static bool TryDeserializePlan(
        string json,
        JsonSerializerOptions options,
        out SalesforceConfigPlan plan,
        out string error)
    {
        plan = null!;
        error = string.Empty;

        try
        {
            var parsed = JsonSerializer.Deserialize<SalesforceConfigPlan>(json, options);
            if (parsed is null)
            {
                error = "JSON was valid but did not match the Salesforce config roadmap shape.";
                return false;
            }

            plan = parsed;
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }
    private async Task<string> ExtractJsonOrRepairAsync(string systemPrompt, string userCommand, string response)
    {
        if (TryExtractJson(response, out var json))
        {
            return json;
        }

        var repairPrompt = $$"""
The previous response did not contain valid JSON.
Convert the Salesforce story below into the exact JSON roadmap schema requested by the system prompt.
Return JSON only. No markdown. No explanation.

Story:
{{userCommand}}

Previous invalid response:
{{response}}
""";

        var repairedResponse = await _deepSeekClient.SendChatAsync(DeepSeekModels.Config, systemPrompt, repairPrompt, 0.0, 6000);
        if (TryExtractJson(repairedResponse, out var repairedJson))
        {
            return repairedJson;
        }

        throw new InvalidOperationException($"The model did not return JSON for the Salesforce config roadmap. First response started with: {PreviewResponse(response)}");
    }
    private static void EnsurePlanDefaults(SalesforceConfigPlan plan)
    {
        plan.Summary ??= string.Empty;
        plan.Requirements ??= new List<SalesforceConfigRequirement>();
        plan.Questions ??= new List<string>();

        foreach (var requirement in plan.Requirements)
        {
            requirement.Id ??= string.Empty;
            requirement.Type ??= string.Empty;
            requirement.Service ??= string.Empty;
            requirement.Operation ??= string.Empty;
            requirement.ObjectApiName ??= string.Empty;
            requirement.FieldApiName ??= string.Empty;
            requirement.FieldType ??= string.Empty;
            requirement.Label ??= string.Empty;
            requirement.DefaultValue ??= string.Empty;
            requirement.InlineHelpText ??= string.Empty;
            requirement.Description ??= string.Empty;
            requirement.FieldDescription ??= string.Empty;
            requirement.Formula ??= string.Empty;
            requirement.FormulaReturnType ??= string.Empty;
            requirement.ExistingFieldApiName ??= string.Empty;
            requirement.TargetMetadataName ??= string.Empty;
            requirement.TargetSectionLabel ??= string.Empty;
            requirement.ReplaceFieldApiName ??= string.Empty;
            requirement.VisibilityConditionSummary ??= string.Empty;
            requirement.PreferredTargetType ??= string.Empty;
            requirement.TargetRegionOrComponent ??= string.Empty;
            requirement.TargetLayoutOrPageLabel ??= string.Empty;
            requirement.ValidationRuleName ??= string.Empty;
            requirement.ErrorMessage ??= string.Empty;
            requirement.ErrorLocation ??= string.Empty;
            requirement.PicklistValues ??= new List<string>();
            requirement.PicklistEntries ??= new List<PicklistValueRequirement>();
            requirement.PicklistRenames ??= new List<PicklistValueRenameRequirement>();
            requirement.PermissionSetNames ??= new List<string>();
            requirement.CustomMetadataTypeApiName ??= string.Empty;
            requirement.RecordDeveloperName ??= string.Empty;
            requirement.CustomMetadataValues ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            requirement.SuggestedFiles ??= new List<string>();
            requirement.SuggestedTriggerEvent ??= string.Empty;
            requirement.SuggestedHelperMethodName ??= string.Empty;
            requirement.ImplementationStrategy ??= string.Empty;
            requirement.ImplementationKind ??= string.Empty;
            requirement.EventInvocation ??= string.Empty;
            requirement.HelperMethodCode ??= string.Empty;
            requirement.TestMethodName ??= string.Empty;
            requirement.TestMethodCode ??= string.Empty;

            foreach (var entry in requirement.PicklistEntries)
            {
                entry.ApiValue ??= string.Empty;
                entry.Label ??= string.Empty;
                entry.ControllingValues ??= new List<string>();
            }

            foreach (var rename in requirement.PicklistRenames)
            {
                rename.CurrentApiValue ??= string.Empty;
                rename.CurrentLabel ??= string.Empty;
                rename.NewLabel ??= string.Empty;
            }

            if (requirement.ProfileAccess is not null)
            {
                requirement.ProfileAccess.EditableProfiles ??= new List<string>();
                requirement.ProfileAccess.ReadOnlyProfiles ??= new List<string>();
            }
        }
    }

    private static void RemoveAcceptanceCriteriaFalsePositives(SalesforceConfigPlan plan)
    {
        plan.Requirements = plan.Requirements
            .Where(requirement => !IsAcceptanceCriteriaNavigationContext(requirement))
            .ToList();
    }

    private static void NormalizeCandidatePersonAccountRequirements(SalesforceConfigPlan plan)
    {
        foreach (var requirement in plan.Requirements)
        {
            var text = string.Join(" ", new[]
            {
                requirement.ObjectApiName,
                requirement.Label,
                requirement.Description,
                requirement.TargetLayoutOrPageLabel,
                requirement.TargetSectionLabel,
                requirement.TargetRegionOrComponent
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

            var mentionsCandidate = ContainsAny(text, "candidate", "candidate record", "candidate page", "candidate layout");
            if (!mentionsCandidate)
            {
                continue;
            }

            if (IsPageRequirement(requirement))
            {
                if (string.IsNullOrWhiteSpace(requirement.TargetLayoutOrPageLabel))
                {
                    requirement.TargetLayoutOrPageLabel = "Candidate Revolution Page";
                }

                if (string.IsNullOrWhiteSpace(requirement.ObjectApiName)
                    || requirement.ObjectApiName.Equals("Contact", StringComparison.OrdinalIgnoreCase)
                    || requirement.ObjectApiName.Equals("Candidate__c", StringComparison.OrdinalIgnoreCase))
                {
                    requirement.ObjectApiName = "Candidate";
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(requirement.ObjectApiName)
                || requirement.ObjectApiName.Equals("Candidate", StringComparison.OrdinalIgnoreCase)
                || requirement.ObjectApiName.Equals("Candidate__c", StringComparison.OrdinalIgnoreCase))
            {
                requirement.ObjectApiName = "Contact";
            }

            if (requirement.FieldApiName.EndsWith("__pc", StringComparison.OrdinalIgnoreCase))
            {
                requirement.FieldApiName = requirement.FieldApiName[..^4] + "__c";
            }
        }
    }

    private static void NormalizeRemainingProfileReadOnlyAccess(SalesforceConfigPlan plan)
    {
        foreach (var requirement in plan.Requirements)
        {
            if (requirement.ProfileAccess is null || requirement.ProfileAccess.ApplyReadOnlyToRemainingProfiles)
            {
                continue;
            }

            var text = string.Join(" ", new[]
            {
                requirement.Label,
                requirement.Description,
                requirement.TargetSectionLabel,
                requirement.TargetRegionOrComponent
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

            if (ImpliesAllOtherProfilesReadOnly(text))
            {
                requirement.ProfileAccess.ApplyReadOnlyToRemainingProfiles = true;
            }
        }
    }

    private static bool ImpliesAllOtherProfilesReadOnly(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return (ContainsAny(text, "read only", "readonly", "read-only", "ready only", "readyonly")
                && ContainsAny(text, "all apart from", "all except", "all other profiles", "all other users", "everyone except", "apart from", "except"))
               || ContainsAny(text,
                   "read only for all apart from",
                   "readonly for all apart from",
                   "read-only for all apart from",
                   "read only for all except",
                   "readonly for all except",
                   "read-only for all except");
    }

    private static void NormalizeMisclassifiedFlowRequirements(SalesforceConfigPlan plan)
    {
        foreach (var requirement in plan.Requirements)
        {
            if (!IsImplementationRequirement(requirement.Type))
            {
                continue;
            }

            var combinedText = string.Join(" ", new[]
            {
                requirement.Label,
                requirement.Description,
                requirement.Service,
                requirement.Operation
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

            if (!LooksLikeFlowChange(combinedText) || LooksLikeExplicitCodeChange(combinedText))
            {
                continue;
            }

            requirement.Type = "flow";
            requirement.Service = "FlowManagementService";
            requirement.Operation = string.IsNullOrWhiteSpace(requirement.Operation) ? "update" : requirement.Operation;

            if (string.IsNullOrWhiteSpace(requirement.ObjectApiName))
            {
                requirement.ObjectApiName = InferObjectApiName(combinedText);
            }

            if (string.IsNullOrWhiteSpace(requirement.FieldApiName))
            {
                requirement.FieldApiName = InferFieldApiName(combinedText);
            }
        }
    }

    private static void SplitCombinedQuickActionConditionalRequirements(SalesforceConfigPlan plan)
    {
        var additionalRequirements = new List<SalesforceConfigRequirement>();

        foreach (var requirement in plan.Requirements)
        {
            if (!IsCombinedQuickActionReplacementAndConditionalDisplay(requirement))
            {
                continue;
            }

            requirement.Label = CleanQuickActionReplacementDescription(requirement.Label);
            requirement.Description = CleanQuickActionReplacementDescription(requirement.Description);
            requirement.TargetRegionOrComponent = CleanQuickActionReplacementDescription(requirement.TargetRegionOrComponent);
            requirement.VisibilityConditionSummary = string.Empty;
            requirement.Operation = "update";

            if (plan.Requirements.Concat(additionalRequirements).Any(IsQuickActionCreateOrConditionalVariant))
            {
                continue;
            }

            additionalRequirements.Add(new SalesforceConfigRequirement
            {
                Id = $"{FirstNonBlank(requirement.Id, $"REQ-{plan.Requirements.Count + additionalRequirements.Count + 1:000}")}-ACTION",
                Type = "quick_action",
                Service = "QuickActionManagementService",
                Operation = "create",
                ObjectApiName = InferQuickActionObject(requirement),
                FieldApiName = requirement.FieldApiName,
                TargetMetadataName = requirement.TargetMetadataName,
                TargetLayoutOrPageLabel = requirement.TargetLayoutOrPageLabel,
                TargetSectionLabel = requirement.TargetSectionLabel,
                TargetRegionOrComponent = requirement.TargetRegionOrComponent,
                Label = "New quick action variant for conditional display.",
                Description = "The story asks for a new action/quick-action variant so the relevant fields display only when the stated visibility conditions are met. Creating new quick actions is outside the current deterministic quick-action scope."
            });
        }

        plan.Requirements.AddRange(additionalRequirements);
    }

    private static void NormalizeIntegrationUserAccessTargets(SalesforceConfigPlan plan, string repoPath)
    {
        if (plan.Requirements.Count == 0 || string.IsNullOrWhiteSpace(repoPath))
        {
            return;
        }

        var availablePermissionSets = GetAvailableMetadataNames(repoPath, "permissionsets", "*.permissionset-meta.xml");
        if (availablePermissionSets.Count == 0)
        {
            return;
        }

        var availableProfiles = GetAvailableMetadataNames(repoPath, "profiles", "*.profile-meta.xml");
        var additionalRequirements = new List<SalesforceConfigRequirement>();
        var requirementsToRemove = new HashSet<SalesforceConfigRequirement>();

        foreach (var requirement in plan.Requirements)
        {
            if (requirement.ProfileAccess is null)
            {
                continue;
            }

            var editablePermissionSets = ResolvePermissionSetAccessTargets(
                requirement.ProfileAccess.EditableProfiles,
                availableProfiles,
                availablePermissionSets);

            var readOnlyPermissionSets = ResolvePermissionSetAccessTargets(
                requirement.ProfileAccess.ReadOnlyProfiles,
                availableProfiles,
                availablePermissionSets);

            if (editablePermissionSets.Count == 0 && readOnlyPermissionSets.Count == 0)
            {
                continue;
            }

            RemoveResolvedNames(requirement.ProfileAccess.EditableProfiles, editablePermissionSets);
            RemoveResolvedNames(requirement.ProfileAccess.ReadOnlyProfiles, readOnlyPermissionSets);

            var permissionSetRequirement = plan.Requirements
                .Concat(additionalRequirements)
                .FirstOrDefault(item =>
                    IsPermissionSetRequirement(item)
                    && NamesEqual(item.ObjectApiName, requirement.ObjectApiName)
                    && NamesEqual(item.FieldApiName, requirement.FieldApiName));

            if (permissionSetRequirement is null)
            {
                permissionSetRequirement = new SalesforceConfigRequirement
                {
                    Id = $"{FirstNonBlank(requirement.Id, $"REQ-{plan.Requirements.Count + additionalRequirements.Count + 1:000}")}-PERMSET",
                    Type = "permission_set",
                    Service = "PermissionSetManagementService",
                    Operation = "update",
                    ObjectApiName = requirement.ObjectApiName,
                    FieldApiName = requirement.FieldApiName,
                    Label = requirement.Label,
                    Description = requirement.Description,
                    PermissionSetNames = new List<string>(),
                    ProfileAccess = new ProfileAccessRequirement()
                };

                additionalRequirements.Add(permissionSetRequirement);
            }

            permissionSetRequirement.PermissionSetNames ??= new List<string>();
            permissionSetRequirement.ProfileAccess ??= new ProfileAccessRequirement();

            AddDistinctNames(permissionSetRequirement.PermissionSetNames, editablePermissionSets);
            AddDistinctNames(permissionSetRequirement.PermissionSetNames, readOnlyPermissionSets);
            AddDistinctNames(permissionSetRequirement.ProfileAccess.EditableProfiles, editablePermissionSets);
            AddDistinctNames(permissionSetRequirement.ProfileAccess.ReadOnlyProfiles, readOnlyPermissionSets);

            if (requirement.Type.Equals("profile_fls_update", StringComparison.OrdinalIgnoreCase)
                && !requirement.ProfileAccess.ApplyReadOnlyToRemainingProfiles
                && requirement.ProfileAccess.EditableProfiles.Count == 0
                && requirement.ProfileAccess.ReadOnlyProfiles.Count == 0)
            {
                requirementsToRemove.Add(requirement);
            }
        }

        if (requirementsToRemove.Count > 0)
        {
            plan.Requirements = plan.Requirements.Where(item => !requirementsToRemove.Contains(item)).ToList();
        }

        if (additionalRequirements.Count > 0)
        {
            plan.Requirements.AddRange(additionalRequirements);
        }
    }

    private static HashSet<string> GetAvailableMetadataNames(string repoPath, string folderName, string searchPattern)
    {
        var directory = Path.Combine(repoPath, "force-app", "main", "default", folderName);
        if (!Directory.Exists(directory))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return Directory.GetFiles(directory, searchPattern)
            .Select(path => Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path)))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> ResolvePermissionSetAccessTargets(
        IEnumerable<string> requestedNames,
        HashSet<string> availableProfiles,
        HashSet<string> availablePermissionSets)
    {
        var resolved = new List<string>();

        foreach (var requestedName in requestedNames.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            if (MatchesMetadataName(availableProfiles, requestedName) || !LooksLikeUserScopedAccess(requestedName))
            {
                continue;
            }

            var resolvedPermissionSet = ResolvePermissionSetName(availablePermissionSets, requestedName);
            if (!string.IsNullOrWhiteSpace(resolvedPermissionSet))
            {
                resolved.Add(resolvedPermissionSet);
            }
        }

        return resolved;
    }

    private static bool LooksLikeUserScopedAccess(string value)
    {
        return value.Contains(" user", StringComparison.OrdinalIgnoreCase)
               || value.Contains(" integration ", StringComparison.OrdinalIgnoreCase)
               || value.EndsWith(" integration", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolvePermissionSetName(HashSet<string> availablePermissionSets, string requestedName)
    {
        var candidates = new[]
        {
            requestedName,
            Regex.Replace(requestedName, @"\buser\b", string.Empty, RegexOptions.IgnoreCase).Trim(),
            Regex.Replace(requestedName, @"\bintegration user\b", "integration", RegexOptions.IgnoreCase).Trim()
        }
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            var match = availablePermissionSets.FirstOrDefault(name => NamesEqual(name, candidate));
            if (!string.IsNullOrWhiteSpace(match))
            {
                return match;
            }
        }

        return string.Empty;
    }

    private static bool MatchesMetadataName(HashSet<string> availableNames, string requestedName)
    {
        return availableNames.Any(name => NamesEqual(name, requestedName));
    }

    private static bool IsPermissionSetRequirement(SalesforceConfigRequirement requirement)
    {
        return requirement.Type.Equals("permission_set", StringComparison.OrdinalIgnoreCase)
               || requirement.Type.Equals("permission_set_fls_update", StringComparison.OrdinalIgnoreCase);
    }

    private static void RemoveResolvedNames(ICollection<string> targetNames, IEnumerable<string> resolvedNames)
    {
        var resolved = resolvedNames.ToList();
        if (resolved.Count == 0)
        {
            return;
        }

        var remaining = targetNames
            .Where(name => !resolved.Any(resolvedName => NamesEqual(resolvedName, name)))
            .ToList();

        targetNames.Clear();
        foreach (var name in remaining)
        {
            targetNames.Add(name);
        }
    }

    private static void AddDistinctNames(ICollection<string> targetNames, IEnumerable<string> values)
    {
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            if (!targetNames.Any(existing => NamesEqual(existing, value)))
            {
                targetNames.Add(value);
            }
        }
    }

    private static bool NamesEqual(string left, string right)
    {
        return NormalizeMetadataName(left) == NormalizeMetadataName(right);
    }

    private static string NormalizeMetadataName(string value)
    {
        return Regex.Replace(value ?? string.Empty, @"[\s_\-\.]", string.Empty).ToLowerInvariant();
    }

    private static bool IsCombinedQuickActionReplacementAndConditionalDisplay(SalesforceConfigRequirement requirement)
    {
        if (!requirement.Type.Equals("quick_action", StringComparison.OrdinalIgnoreCase)
            && !requirement.Service.Equals("QuickActionManagementService", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(requirement.FieldApiName)
            || string.IsNullOrWhiteSpace(FirstNonBlank(requirement.ReplaceFieldApiName, requirement.ExistingFieldApiName)))
        {
            return false;
        }

        var text = $"{requirement.Label} {requirement.Description} {requirement.VisibilityConditionSummary} {requirement.TargetSectionLabel} {requirement.TargetRegionOrComponent}";
        return ContainsAny(text, "only visible", "visible only", "display depending", "depending on", "new action", "new quick action", "create a new action");
    }

    private static bool IsQuickActionCreateOrConditionalVariant(SalesforceConfigRequirement requirement)
    {
        if (!requirement.Type.Equals("quick_action", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var text = $"{requirement.Operation} {requirement.Label} {requirement.Description}";
        return requirement.Operation.Equals("create", StringComparison.OrdinalIgnoreCase)
               || ContainsAny(text, "new action", "new quick action", "conditional display", "display only", "visible only", "only visible");
    }

    private static bool IsPageRequirement(SalesforceConfigRequirement requirement)
    {
        return requirement.Type.Equals("layout", StringComparison.OrdinalIgnoreCase)
               || requirement.Type.Equals("flexipage", StringComparison.OrdinalIgnoreCase)
               || requirement.Type.Equals("quick_action", StringComparison.OrdinalIgnoreCase)
               || requirement.Service.Equals("LayoutManagementService", StringComparison.OrdinalIgnoreCase)
               || requirement.Service.Equals("FlexipageManagementService", StringComparison.OrdinalIgnoreCase)
               || requirement.Service.Equals("QuickActionManagementService", StringComparison.OrdinalIgnoreCase);
    }

    private static string CleanQuickActionReplacementDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return description;
        }

        var sentences = Regex.Split(description, @"(?<=[.!?])\s+")
            .Where(sentence => !ContainsAny(sentence, "only visible", "visible only", "display depending", "depending on", "new action", "new quick action", "create a new action"))
            .Select(sentence => sentence.Trim())
            .Where(sentence => !string.IsNullOrWhiteSpace(sentence))
            .ToList();

        var cleaned = sentences.Count == 0 ? description : string.Join(" ", sentences);
        cleaned = RemoveConditionalDisplayClause(cleaned);
        return string.IsNullOrWhiteSpace(cleaned) ? description : cleaned.Trim();
    }

    private static string RemoveConditionalDisplayClause(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var patterns = new[]
        {
            @"\s*,?\s*visible\s+only\s+if\b.*$",
            @"\s*,?\s*only\s+visible\s+if\b.*$",
            @"\s*,?\s*display(?:ed)?\s+depending\s+on\b.*$",
            @"\s*,?\s*depending\s+on\b.*$",
            @"\s*,?\s*with\s+conditional\s+visibility\b.*$"
        };

        var cleaned = value;
        foreach (var pattern in patterns)
        {
            cleaned = Regex.Replace(cleaned, pattern, string.Empty, RegexOptions.IgnoreCase);
        }

        return cleaned.Trim().TrimEnd(',', ';', ':', '-').Trim();
    }

    private static string InferQuickActionObject(SalesforceConfigRequirement requirement)
    {
        if (!string.IsNullOrWhiteSpace(requirement.TargetMetadataName)
            && requirement.TargetMetadataName.Contains('.', StringComparison.Ordinal))
        {
            return requirement.TargetMetadataName.Split('.')[0].Trim();
        }

        var text = $"{requirement.Label} {requirement.Description} {requirement.TargetSectionLabel} {requirement.TargetRegionOrComponent} {requirement.TargetLayoutOrPageLabel}";
        if (ContainsAny(text, "organisation", "organization", "account", "organisation details", "organization details"))
        {
            return "Account";
        }

        return requirement.ObjectApiName;
    }

    private static string FirstNonBlank(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static bool IsImplementationRequirement(string type)
    {
        return string.Equals(type, "implementation_code", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeFlowChange(string value)
    {
        return ContainsAny(value, "flow", "record-triggered", "screen flow", "created via a flow", "flow logic");
    }

    private static bool LooksLikeExplicitCodeChange(string value)
    {
        return ContainsAny(value, "apex", "trigger", "lwc", "aura", "visualforce", "javascript", ".cls", ".trigger");
    }

    private static string InferObjectApiName(string value)
    {
        if (ContainsAny(value, "organisation", "organization", "account"))
        {
            return "Account";
        }

        if (ContainsAny(value, "placement"))
        {
            return "Placement__c";
        }

        if (ContainsAny(value, "supplier"))
        {
            return "Supplier__c";
        }

        if (ContainsAny(value, "candidate"))
        {
            return "Candidate";
        }

        if (ContainsAny(value, "contact"))
        {
            return "Contact";
        }

        return string.Empty;
    }

    private static string InferFieldApiName(string value)
    {
        var explicitApiName = Regex.Match(value, @"\b[A-Za-z][A-Za-z0-9_]*__c\b");
        if (explicitApiName.Success)
        {
            return explicitApiName.Value;
        }

        var fieldMatch = Regex.Match(
            value,
            @"field\s+['""“”]?(?<name>[A-Za-z][A-Za-z0-9\s/&-]{2,80}?)(?:['""“”]?(\s|$|\.|,|;|:))",
            RegexOptions.IgnoreCase);

        if (!fieldMatch.Success)
        {
            return string.Empty;
        }

        var name = fieldMatch.Groups["name"].Value.Trim();
        name = Regex.Replace(name, @"\s+(to|with|when|on|in|is|should|must|needs)\b.*$", string.Empty, RegexOptions.IgnoreCase).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var words = Regex.Matches(name, @"[A-Za-z0-9]+")
            .Select(match => match.Value)
            .Where(word => !string.IsNullOrWhiteSpace(word))
            .Select(word => char.ToUpperInvariant(word[0]) + (word.Length > 1 ? word[1..] : string.Empty))
            .ToList();

        return words.Count == 0 ? string.Empty : $"{string.Join("_", words)}__c";
    }

    private static bool IsAcceptanceCriteriaNavigationContext(SalesforceConfigRequirement requirement)
    {
        if (!IsLayoutOrFlexipage(requirement.Type))
        {
            return false;
        }

        var combinedText = string.Join(" ", new[]
        {
            requirement.Description,
            requirement.TargetLayoutOrPageLabel,
            requirement.TargetSectionLabel,
            requirement.TargetRegionOrComponent,
            requirement.VisibilityConditionSummary
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        if (string.IsNullOrWhiteSpace(combinedText))
        {
            return false;
        }

        var hasConcreteMetadataInstruction =
            !string.IsNullOrWhiteSpace(requirement.ReplaceFieldApiName) ||
            !string.IsNullOrWhiteSpace(requirement.TargetMetadataName) ||
            ContainsAny(combinedText, "add field", "remove field", "replace field", "move field", "rename field", "change layout", "update layout", "update flexipage", "conditional visibility", "visibility rule", "component visibility");

        if (hasConcreteMetadataInstruction)
        {
            return false;
        }

        return ContainsAny(
            combinedText,
            "acceptance criteria",
            "pre-requisite",
            "pre-requisites",
            "create perm",
            "create contract",
            "check that",
            "should show",
            "should not show",
            "does not show",
            "is visible",
            "isn't visible",
            "only visible",
            "tab");
    }

    private static bool IsLayoutOrFlexipage(string type)
    {
        return string.Equals(type, "layout", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "flexipage", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAny(string source, params string[] needles)
    {
        return needles.Any(needle => source.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }
    private static string BuildSystemPrompt(string repoPath)
    {
        var profiles = Directory.Exists(Path.Combine(repoPath, "force-app", "main", "default", "profiles"))
            ? Directory.GetFiles(Path.Combine(repoPath, "force-app", "main", "default", "profiles"), "*.profile-meta.xml")
                .Select(Path.GetFileNameWithoutExtension)
                .Select(name => name?.Replace(".profile-meta", string.Empty) ?? string.Empty)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name)
                .ToList()
            : new List<string>();

        var objects = Directory.Exists(Path.Combine(repoPath, "force-app", "main", "default", "objects"))
            ? Directory.GetDirectories(Path.Combine(repoPath, "force-app", "main", "default", "objects"))
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name)
                .Take(250)
                .ToList()
            : new List<string?>();

        var quickActions = Directory.Exists(Path.Combine(repoPath, "force-app", "main", "default", "quickActions"))
            ? Directory.GetFiles(Path.Combine(repoPath, "force-app", "main", "default", "quickActions"), "*.quickAction-meta.xml")
                .Select(path =>
                {
                    try
                    {
                        var xml = System.Xml.Linq.XDocument.Load(path);
                        var ns = xml.Root?.Name.Namespace ?? System.Xml.Linq.XNamespace.None;
                        var label = xml.Root?.Element(ns + "label")?.Value ?? string.Empty;
                        var name = Path.GetFileName(path).Replace(".quickAction-meta.xml", string.Empty, StringComparison.OrdinalIgnoreCase);
                        return string.IsNullOrWhiteSpace(label) ? name : $"{name} ({label})";
                    }
                    catch
                    {
                        return Path.GetFileName(path).Replace(".quickAction-meta.xml", string.Empty, StringComparison.OrdinalIgnoreCase);
                    }
                })
                .OrderBy(name => name)
                .ToList()
            : new List<string>();
        return $$"""
You are a Salesforce metadata requirements analyst for a source-format SFDX repository.
Your job is to extract ALL explicit requirements from the user story/request, then classify which ones are Salesforce CONFIG metadata work and which ones are Salesforce implementation-code work.
Do not produce full files. Only include small code fragments for simple deterministic implementation-code requirements when you are confident they are valid.

Available profile names:
{{string.Join(", ", profiles)}}

Known object folders:
{{string.Join(", ", objects)}}

Known quick actions:
{{string.Join(", ", quickActions)}}

Return JSON only, no markdown fences, using this shape:
{
  "summary": "short human-readable summary",
  "questions": ["only genuine blockers or ambiguity"],
  "requirements": [
    {
      "id": "REQ-001",
      "type": "field_create | field_update | field_upsert | profile_fls_update | validation_rule | record_type | picklist | layout | flexipage | flow | quick_action | permission_set | custom_metadata | custom_permission | custom_label | global_value_set | external_dependency | implementation_code | unsupported_requirement",
      "service": "ObjectManagementService | ProfileManagementService | LayoutManagementService | FlexipageManagementService | FlowManagementService | QuickActionManagementService | PermissionSetManagementService | CustomMetadataManagementService | CustomPermissionManagementService | LabelManagementService | GlobalValueSetManagementService | RecordTypeManagementService | CodeEditService",
      "operation": "create | update | upsert",
      "objectApiName": "Placement__c",
      "fieldApiName": "Testing_Hello__c",
      "fieldType": "Text | Checkbox | Number | Picklist | LongTextArea | Formula",
      "label": "Testing Hello",
      "length": 255,
      "required": false,
      "defaultValue": "",
      "inlineHelpText": "help text",
      "description": "plain-language requirement summary, not Salesforce metadata description",
      "fieldDescription": "Salesforce field <description> only when the story explicitly asks to create or update the field description",
      "formula": "",
      "formulaReturnType": "",
      "existingFieldApiName": "",
      "targetMetadataName": "",
      "targetSectionLabel": "",
      "replaceFieldApiName": "",
      "visibilityConditionSummary": "",
      "preferredTargetType": "layout | flexipage",
      "targetRegionOrComponent": "",
      "targetLayoutOrPageLabel": "",
      "validationRuleName": "",
      "errorMessage": "",
      "errorLocation": "",
      "picklistEntries": [
        { "apiValue": "client-site", "label": "Client Site", "default": false, "controllingValues": [] }
      ],
      "picklistRenames": [
        { "currentApiValue": "Existing API Value", "currentLabel": "Existing Label", "newLabel": "New Label" }
      ],
      "keepPicklistValuesInOrder": false,
      "addGlobalValueSetValuesToAllRecordTypes": false,
      "permissionSetNames": ["InTime Integration"],
      "customMetadataTypeApiName": "Document_Name_overrides_for_Sites__mdt",
      "recordDeveloperName": "External_checks_Document_P2",
      "customMetadataValues": {
        "Field_API_Name__c": "External_checks_Document__c",
        "Field_Label__c": "External checks Document",
        "Field_Value__c": "Whitelist",
        "Site__c": "P2"
      },
      "suggestedFiles": [
        "force-app/main/default/classes/AccountTriggerHandler.cls",
        "force-app/main/default/classes/AccountTriggerHandlerTest.cls",
        "force-app/main/default/lwc/extensionNotesPanel/extensionNotesPanel.js",
        "force-app/main/default/lwc/extensionNotesPanel/extensionNotesPanel.html"
      ],
      "implementationKind": "apex_trigger_handler | apex_class | apex_service | apex_trigger | lwc | aura | visualforce | javascript | unknown",
      "implementationStrategy": "short strategy for how the code should be changed",
      "suggestedTriggerEvent": "beforeInsert | beforeUpdate | afterInsert | afterUpdate",
      "suggestedHelperMethodName": "defaultClientInvoiceConsolidation",
      "eventInvocation": "defaultClientInvoiceConsolidation((List<Account>) newList);",
      "helperMethodCode": "private void defaultClientInvoiceConsolidation(List<Account> records) { ... }",
      "testMethodName": "testDefaultClientInvoiceConsolidation",
      "testMethodCode": "@IsTest static void testDefaultClientInvoiceConsolidation() { ... }",
      "requiresSecondAiPass": true,
      "profileAccess": {
        "editableProfiles": ["Admin"],
        "readOnlyProfiles": ["Recruiter"],
        "applyReadOnlyToRemainingProfiles": false
      }
    }
  ]
}

Rules:
- Extract EVERY explicit requirement from the story, not only the ones this app can implement.
- Coverage depends on complete extraction. Do not silently drop stated page layout, flexipage, quick action, flow, permission set, validation rule, picklist, field, record type, label, global value set, custom metadata, custom permission, Apex, LWC, Aura, Visualforce, JavaScript, manual process, or external dependency requirements.
- If a requirement is stated but the exact metadata file/action is unclear, still output the requirement with the best type/service and put the uncertainty in questions.
- If a requirement says it is handled by another ticket / in development elsewhere / no action required here, output it as type "external_dependency" with service "" so coverage can show it as unsupported/discarded rather than invisible.
- Treat Acceptance Criteria, Pre-requisites, testing steps, and instructions like "Create placement", "Check that", "on the Invoice Details tab", "on the Onboarding tab", or "field is visible/not visible" as validation/testing context, not metadata change requirements, unless the same sentence explicitly asks to add, remove, replace, move, rename, create, or update a layout/flexipage/quick action/field.
- Do not output layout or flexipage requirements just because acceptance criteria mention where a user verifies a field, which tab it appears on, or why a field is visible. Visibility context is only a flexipage requirement when the story explicitly asks to change the visibility rule or component behavior.
- If a requirement needs Apex, LWC, Aura, Visualforce, JavaScript, or other repo code, output it as type "implementation_code" with service "CodeEditService" rather than dropping it.
- If the story asks to update Flow logic/configuration, keep it as type "flow" with service "FlowManagementService". Do not convert Flow work into implementation_code unless the story explicitly asks to implement it in Apex/LWC/Aura/Visualforce/JavaScript.
- If FlowManagementService is not supported, the app will separately assess whether an Apex alternative can be offered. The extraction step must preserve the original requested implementation as Flow.
- For implementation_code, always set implementationKind. Use "lwc" for Lightning Web Component changes, "apex_class" for normal Apex class changes, "apex_trigger_handler" for Apex TriggerHandler pattern changes, "apex_service" for service/helper class changes, "visualforce" for .page/.component changes, and "unknown" only when the technology is genuinely unclear.
- For implementation_code, include suggestedFiles when the story gives component/class names or when the target can be inferred from object/field/feature names. Use source-format repo paths such as force-app/main/default/classes/MyClass.cls or force-app/main/default/lwc/myComponent/myComponent.js.
- Do not force implementation code into TriggerHandler. Only use implementationKind "apex_trigger_handler" when the business rule is record-trigger timing logic or the story explicitly needs trigger-style automation.
- For simple Apex trigger-handler alternatives, include suggestedTriggerEvent, suggestedHelperMethodName, eventInvocation, helperMethodCode, testMethodName, and testMethodCode only when you can provide complete valid Apex fragments without ellipses/placeholders. Otherwise leave code fragments blank and set requiresSecondAiPass=true.
- For generic Apex or LWC changes, usually set requiresSecondAiPass=true. The code editor will use suggestedFiles to send a small focused prompt and perform surgical edits.
- If implementation_code is explicit but no likely files can be inferred, leave suggestedFiles empty and explain the missing context in questions.
- If a requirement needs manual deployment/process or a service not listed here and is not repo code, output it as "unsupported_requirement" with service "" rather than dropping it.
- For page layout changes, output type "layout" and service "LayoutManagementService". Include objectApiName, fieldApiName, operation, targetSectionLabel, targetLayoutOrPageLabel or targetMetadataName, and a plain-language description.
- For every layout, flexipage, or quick_action requirement, always populate objectApiName with the business object being edited. If the story says Organisation or Organization, use Account. If it says Contract Placement or Placement, use Placement__c. If it says Supplier, use Supplier__c.
- For every layout, flexipage, or quick_action requirement, always preserve the exact page/layout/action name from the story in targetLayoutOrPageLabel. If the story says "page layout [Organisation Revolution Page]" or "Organisation Revolution Page", set targetLayoutOrPageLabel to "Organisation Revolution Page".
- For every layout, flexipage, or quick_action requirement, always preserve the exact section/tab/subtab path in targetSectionLabel or targetRegionOrComponent, for example "Admin Tab > Billing sub tab > Contract Billing" or "Admin Tab > Billing sub tab > Perm Billing".
- If screenshots/images contain highlighted field names, extract those field labels/API names into the description and use fieldApiName/replaceFieldApiName when the requirement is a single field replacement. Do not ignore image-derived field names.
- If a story gives a named Lightning page/flexipage such as "Organisation Revolution Page", classify visibility/component criteria changes as type "flexipage" with preferredTargetType "flexipage", objectApiName, and targetLayoutOrPageLabel populated.
- If a story asks to rearrange multiple fields shown in a screenshot but exact target order cannot be confidently extracted, still output a flexipage or layout requirement with targetLayoutOrPageLabel and targetSectionLabel populated, but describe that exact image-derived field order is required.
- If a layout requirement replaces one field with another, put the old field in replaceFieldApiName and the new field in fieldApiName.
- If a layout requirement adds a field to a section, put the section label in targetSectionLabel.
- If a layout/flexipage requirement asks to create a new layout or flexipage, keep operation "create"; coverage will mark it unsupported.
- For dynamic visibility, output type "flexipage" and service "FlexipageManagementService". Include objectApiName, fieldApiName, visibilityConditionSummary, targetLayoutOrPageLabel if known, and a plain-language description.
- For visibility removal on multiple fields, include every affected field API name in visibilityConditionSummary or description. Example: "Remove visibility criteria from Billing_Contact_Email__c, Invoice_Consolidation_Option__c, Minimum_Monthly_Spend__c on Organisation Revolution Page." This app can only remove visibility safely when explicit field API names are available.
- For flexipage field-reference replacement, put the old field in replaceFieldApiName and the new field in fieldApiName.
- If a named Revolution Page contains a field replacement inside an embedded related-record/quick-action section, classify that replacement as quick_action when the section/action label is available; keep the page name in targetRegionOrComponent or description for traceability.
- For quick action creation or updates, output type "quick_action" with QuickActionManagementService, not flexipage.
- If a story says "Page Layout" but describes a section that displays fields from another record/object, especially wording like "section displays Organisation fields called Organisation Details", classify editable field changes inside that section as quick_action updates when a matching known quick action exists.
- If the target section title matches a known quick action label, set targetMetadataName to that quick action metadata name, for example "Account.Organisation_Details", and targetLayoutOrPageLabel to the label, for example "Organisation Details".
- For Organisation/Organization quick actions, use objectApiName "Account" because Organisation metadata is stored on Account.
- If a requirement says a new quick action is needed, output a separate quick_action requirement with operation "create"; do not hide it inside a supported update requirement.
- If a request creates a field and also mentions profile FLS, output two requirements: field_create and profile_fls_update.
- If a request mentions permission set access, output type "permission_set", service "PermissionSetManagementService", objectApiName, fieldApiName, and permissionSetNames. Do not put permission set names in profileAccess.
- If the request says read/write, editable must be true.
- If the request says readonly/read only/readyonly/read-only, editable must be false.
- Only include profiles explicitly requested unless the user clearly says all other/remaining profiles.
- If the user asks "all other profiles read only", set applyReadOnlyToRemainingProfiles=true.
- Resolve profile names only to the available profile names above.
- Use custom field API names ending __c. Convert informal names like testing_hello or test sync to Testing_Hello__c or Test_Sync__c.
- Use custom object API names ending __c. Convert placement__c to Placement__c.
- For checkboxes, include defaultValue "false" unless the user specifies another value.
- If a field already exists and the user asks to create it, still output field_create; the app will safely block it.
- For adding picklist values, output type "picklist", objectApiName, fieldApiName, and picklistEntries with exact apiValue and label. If the story says the values must be available under a controlling picklist value, populate controllingValues on each affected picklist entry with that controlling value label exactly as stated. If the story explicitly says the new picklist values must be "in order", "sorted", "alphabetical", or similar, set keepPicklistValuesInOrder true; otherwise leave it false.
- For global value set changes, output type "global_value_set", service "GlobalValueSetManagementService", targetMetadataName, picklistEntries for NEW values, and picklistRenames for label-only renames. If the story says "rename should be done on labels only", keep currentApiValue as the existing value/API name and put only the revised user-facing label in newLabel. Do not put label-only renames in picklistEntries. For new global value set values, use ordered insertion; if the story says the new values should be in order, also set keepPicklistValuesInOrder true. If the story says new values should be added to all record types for any fields that use the global picklist, set addGlobalValueSetValuesToAllRecordTypes true.
- For custom label changes, output type "custom_label", service "LabelManagementService", targetMetadataName as the label API name, and defaultValue as the label value.
- For custom metadata record create/update changes, output type "custom_metadata", service "CustomMetadataManagementService", customMetadataTypeApiName as the custom metadata type API name, recordDeveloperName as the row/developer name, label as the record label, and customMetadataValues as an object containing exact metadata field API names and values. If the story says Field API Name, Field Label, Field Value, and Site, map those to Field_API_Name__c, Field_Label__c, Field_Value__c, and Site__c unless the story gives exact field API names.
- If a custom metadata requirement only says "update records in attached spreadsheet" or gives a summary without exact record developer names and field/value pairs, output it as unsupported_requirement rather than custom_metadata.
- For custom permission changes, output type "custom_permission", service "CustomPermissionManagementService", targetMetadataName, label, and description.
- For record type picklist value updates, output type "record_type", service "RecordTypeManagementService", objectApiName, targetMetadataName as the record type developer name, fieldApiName as the picklist field, and picklistEntries.
- For formula field updates, output type "field_update", fieldType "Formula", objectApiName, fieldApiName, and formula. Do not populate fieldDescription unless the story explicitly asks to create or update the Salesforce field description/help description; use description only as a human-readable requirement summary.
- For validation rules, output type "validation_rule", objectApiName, validationRuleName, formula, errorMessage, errorLocation, and fieldApiName when the error is under a field. If the "Error message location" specifies a field (e.g., "Field Client Invoice Consolidation"), extract the field name into fieldApiName.
- Validation rule extraction is all-or-nothing: if the story contains Error Condition formula, Error Message, or Error message location labels, copy those values exactly into formula, errorMessage, and errorLocation. Do not omit them.
- If a validation rule is requested but the formula is not available in the story, output it as type "unsupported_requirement" with a clear description instead of a supported validation_rule.
- Do not invent missing implementation details; add a question, but still extract the requirement if the story clearly asks for it.
- Never use the description property to request Salesforce <description> metadata changes. For field metadata, only fieldDescription may write <description>, and only when explicitly requested by the story.
- If a story mentions Apex, LWC, Aura, Visualforce, or JavaScript as context, still extract Salesforce config requirements and output implementation_code only when the story explicitly asks for code behaviour to change.
""";
    }

    private static bool TryExtractJson(string response, out string json)
    {
        json = string.Empty;
        if (string.IsNullOrWhiteSpace(response))
        {
            return false;
        }

        var fenced = Regex.Match(response, "```(?:json)?\\s*(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (fenced.Success)
        {
            json = fenced.Groups[1].Value.Trim();
            return true;
        }

        var firstBrace = response.IndexOf('{');
        var lastBrace = response.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            json = response[firstBrace..(lastBrace + 1)].Trim();
            return true;
        }

        return false;
    }

    private static string PreviewResponse(string response)
    {
        var preview = string.Join(" ", (response ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return preview.Length <= 240 ? preview : preview[..240] + "...";
    }
}





