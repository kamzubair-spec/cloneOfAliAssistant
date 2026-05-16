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
            "profile", "permission set", "permission", "custom permission", "fls", "field level security", "access"
        };

        return configKeywords.Any(keyword => userCommand.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<SalesforceConfigPlan> AnalyzeAsync(string repoPath, string userCommand)
    {
        var systemPrompt = BuildSystemPrompt(repoPath);
        var response = await _deepSeekClient.SendChatAsync(DeepSeekModels.Config, systemPrompt, userCommand, 0.0, 6000);
        var plan = await ParsePlanResponseAsync(systemPrompt, userCommand, response);
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

        var systemPrompt = """
You are testing Jira screenshot reading for a Salesforce delivery assistant.
Read the inline images carefully and report only what you can actually see.
Return a concise plain-text diagnostic.
""";

        var userContent = BuildVisionContent(storyContent)
            .Concat(new[]
            {
                new AiChatContentPart(
                    AiChatContentKind.Text,
                    "Diagnostic request: What did you read from the inline Jira images?")
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
                if (string.IsNullOrWhiteSpace(dataUrl)) continue;

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
        return content.Count == 0 ? new[] { new AiChatContentPart(AiChatContentKind.Text, storyContent.PlainText) } : content;
    }

    private static string TryBuildImageDataUrl(string path, string mimeType)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return string.Empty;
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0 || bytes.Length > 10_000_000) return string.Empty;
            var resolvedMimeType = string.IsNullOrWhiteSpace(mimeType) ? "image/png" : mimeType;
            return $"data:{resolvedMimeType};base64,{Convert.ToBase64String(bytes)}";
        }
        catch { return string.Empty; }
    }

    private async Task<SalesforceConfigPlan> DeserializePlanOrRepairAsync(string systemPrompt, string userCommand, string json, JsonSerializerOptions options)
    {
        if (TryDeserializePlan(json, options, out var plan, out var parseError)) return plan;
        var repairPrompt = "The previous response contained malformed JSON. Repair it into the correct schema.\n\nError: " + parseError + "\n\nJSON: " + json;
        var repairedResponse = await _deepSeekClient.SendChatAsync(DeepSeekModels.Config, systemPrompt, repairPrompt, 0.0, 6000);
        if (TryExtractJson(repairedResponse, out var repairedJson) && TryDeserializePlan(repairedJson, options, out plan, out _)) return plan;
        throw new InvalidOperationException($"Repair failed: {parseError}");
    }

    private static bool TryDeserializePlan(string json, JsonSerializerOptions options, out SalesforceConfigPlan plan, out string error)
    {
        plan = null!; error = string.Empty;
        try { plan = JsonSerializer.Deserialize<SalesforceConfigPlan>(json, options)!; return plan != null; }
        catch (JsonException ex) { error = ex.Message; return false; }
    }

    private async Task<string> ExtractJsonOrRepairAsync(string systemPrompt, string userCommand, string response)
    {
        if (TryExtractJson(response, out var json)) return json;
        var repairPrompt = "Extract valid JSON from the following response:\n\n" + response;
        var repairedResponse = await _deepSeekClient.SendChatAsync(DeepSeekModels.Config, systemPrompt, repairPrompt, 0.0, 6000);
        if (TryExtractJson(repairedResponse, out var repairedJson)) return repairedJson;
        throw new InvalidOperationException("Failed to extract JSON.");
    }

    private static void EnsurePlanDefaults(SalesforceConfigPlan plan)
    {
        plan.Summary ??= string.Empty;
        plan.Requirements ??= new List<SalesforceConfigRequirement>();
        plan.Questions ??= new List<string>();
        foreach (var req in plan.Requirements) { req.Id ??= ""; req.Type ??= ""; req.Description ??= ""; req.PermissionSetNames ??= new List<string>(); }
    }

    private static string BuildSystemPrompt(string repoPath)
    {
        var metadataRoot = Path.Combine(repoPath, "force-app", "main", "default");
        var profiles = LoadMetadataNames(Path.Combine(metadataRoot, "profiles"), "*.profile-meta.xml", ".profile-meta");
        var permissionSets = LoadMetadataNames(Path.Combine(metadataRoot, "permissionsets"), "*.permissionset-meta.xml", ".permissionset-meta");
        var customPermissions = LoadMetadataNames(Path.Combine(metadataRoot, "customPermissions"), "*.customPermission-meta.xml", ".customPermission-meta");

        return $$"""
You are a Salesforce Permission Analyst. Your ONLY job is to extract requirements related to Profiles, Permission Sets, and Custom Permissions.
The system is NOT configured for any other metadata types.

Supported Work:
1. Profile Updates (FLS, Tab Visibility, Apex Class Access, Object Permissions, User Permissions, etc.)
2. Permission Set Updates (FLS, Object Permissions, etc.)
3. Custom Permission Creation/Updates

If a requirement is NOT for a Profile, Permission Set, or Custom Permission, you MUST classify it as "unsupported_requirement" with a description saying "System is not configured for this requirement."

Available profile names:
{{string.Join(", ", profiles)}}

Available permission set names:
{{string.Join(", ", permissionSets)}}

Available custom permission names:
{{string.Join(", ", customPermissions)}}

Return JSON only:
{
  "summary": "...",
  "questions": [],
  "requirements": [
    {
      "id": "REQ-001",
      "type": "profile_metadata | permission_set | custom_permission | unsupported_requirement",
      "operation": "create | update",
      "targetMetadataName": "Metadata name",
      "label": "Label",
      "description": "...",
      "permissionType": "fls | tab | apex_class | apex_page | object | custom_permission | record_type | application | user_permission",
      "objectApiName": "Account",
      "fieldApiName": "Industry",
      "permissionValue": "true/false or visibility",
      "permissionSetNames": [],
      "profileAccess": { "editableProfiles": [], "readOnlyProfiles": [], "applyReadOnlyToRemainingProfiles": false }
    }
  ]
}

Rules:
- Only extract profile, permission set, and custom permission work.
- If the story asks for any other metadata, automation, code, layout, flow, object, field, validation rule, page, or anything outside those three families, return "unsupported_requirement".
- For unsupported items, set the description to exactly "{{PermissionToolingCatalog.UnsupportedRequirementMessage}}".
- Use existing metadata API/file names when they are already present in the repo lists above.
- For FLS: permissionType "fls".
- For Tabs: permissionType "tab", permissionValue "DefaultOn|DefaultOff|Hidden".
- For Apex: permissionType "apex_class" or "apex_page", permissionValue "true|false".
- For Object: permissionType "object", permissionValue e.g. "Read,Create,Edit".
- For User Perms: permissionType "user_permission".
- EVERY other request MUST be "unsupported_requirement".
""";
    }

    private static List<string> LoadMetadataNames(string directory, string searchPattern, string suffixToTrim)
    {
        if (!Directory.Exists(directory))
        {
            return new List<string>();
        }

        return Directory.GetFiles(directory, searchPattern)
            .Select(Path.GetFileNameWithoutExtension)
            .Select(name => name?.Replace(suffixToTrim, string.Empty, StringComparison.OrdinalIgnoreCase) ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryExtractJson(string response, out string json)
    {
        json = string.Empty;
        if (string.IsNullOrWhiteSpace(response)) return false;
        var fenced = Regex.Match(response, "```(?:json)?\\s*(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (fenced.Success) { json = fenced.Groups[1].Value.Trim(); return true; }
        var firstBrace = response.IndexOf('{');
        var lastBrace = response.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace) { json = response[firstBrace..(lastBrace + 1)].Trim(); return true; }
        return false;
    }
}
