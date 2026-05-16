using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using eZBERP_AI_IDE.Models;
using File = System.IO.File;

namespace eZBERP_AI_IDE.Services;

public sealed class CodeEditService : IConfigWorkItemHandler
{
    private readonly RepoContextService _repoContext;
    private readonly DeepSeekClient _deepSeek;
    private readonly Action<string>? _progress;

    public CodeEditService(RepoContextService repoContext, DeepSeekClient deepSeek, Action<string>? progress = null)
    {
        _repoContext = repoContext;
        _deepSeek = deepSeek;
        _progress = progress;
    }

    public string ServiceName => nameof(CodeEditService);

    public bool CanHandle(SalesforceConfigRequirement requirement)
    {
        return requirement.Type.Equals("implementation_code", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<FileChangeSet?> BuildChangeSetAsync(string repoPath, SalesforceConfigRequirement requirement)
    {
        if (!CanHandle(requirement)) return null;

        var targetName = FirstNonBlank(requirement.ObjectApiName, requirement.FieldApiName, requirement.Id);
        Report($"Resolving Apex context for {targetName}...");
        var relevantFiles = ResolveSuggestedFiles(repoPath, requirement);
        if (relevantFiles.Count > 0)
        {
            Report("Using hinted implementation file(s): " + FormatFileListForLog(relevantFiles));
        }
        else
        {
            Report($"No hinted implementation files found. Expanding Apex context for {targetName}...");
            relevantFiles = await _repoContext.FindRelevantFilesAsync(repoPath, requirement.Description);
        }

        if (relevantFiles.Count == 0)
        {
            return new FileChangeSet("Code implementation plan", new List<FileChangeProposal>(), new[] { $"No relevant files found for requirement: {requirement.Id}" });
        }

        Report($"Found {relevantFiles.Count} relevant code file(s). Reading context...");
        var fileContents = new Dictionary<string, string>();
        foreach (var file in relevantFiles)
        {
            var content = await ReadExistingContentAsync(repoPath, file);
            if (!string.IsNullOrEmpty(content))
            {
                fileContents[file] = content;
            }
        }

        if (fileContents.Count == 0)
        {
            return new FileChangeSet("Code implementation plan", new List<FileChangeProposal>(), new[] { $"Relevant files found but they are empty or missing: {requirement.Id}" });
        }

        Report("Code edit context includes: " + FormatFileListForLog(fileContents.Keys));

        var deterministicFirstChangeSet = await TryBuildDeterministicTriggerHandlerChangeSetAsync(
            repoPath,
            requirement,
            fileContents,
            Array.Empty<string>());
        if (deterministicFirstChangeSet is not null)
        {
            return deterministicFirstChangeSet;
        }

        Report("Using generic surgical code-edit path for " + FirstNonBlank(requirement.ImplementationKind, "implementation code") + ".");

        var systemPrompt = BuildSurgicalEditSystemPrompt();

        var userMessage = $@"Requirement: {requirement.Description}

Implementation Hints:
{BuildImplementationHintText(requirement)}

Relevant Files:
{string.Join("\n\n", fileContents.Select(kvp => $"--- {kvp.Key} ---\n{kvp.Value}"))}";

        Report($"Asking AI for surgical code edits for {requirement.Id}...");
        var plansResult = await RequestSurgicalEditPlansAsync(systemPrompt, userMessage, requirement.Id);
        if (!plansResult.IsSuccess)
        {
            var deterministicChangeSet = await TryBuildDeterministicTriggerHandlerChangeSetAsync(
                repoPath,
                requirement,
                fileContents,
                plansResult.Messages);
            if (deterministicChangeSet is not null)
            {
                return deterministicChangeSet;
            }

            return new FileChangeSet("Code implementation plan", new List<FileChangeProposal>(), plansResult.Messages);
        }

        Report("AI selected surgical target(s): " + FormatEditPlanSummary(plansResult.Plans));

        var testGuardMessages = new List<string>();
        var plans = await EnsureApexTestPlansAsync(repoPath, requirement, fileContents, systemPrompt, plansResult.Plans, testGuardMessages);
        Report("Final surgical targets after test guard: " + FormatEditPlanSummary(plans));
        Report($"Validating {plans.Sum(plan => plan.Edits.Count)} surgical edit block(s)...");

        try
        {
            return WithMessages(await BuildSurgicalChangeSetAsync(repoPath, plans), testGuardMessages);
        }
        catch (Exception ex)
        {
            if (!IsRepairableSurgicalEditFailure(ex))
            {
                return new FileChangeSet("Code implementation plan", new List<FileChangeProposal>(), new[] { $"Surgical edit failed for requirement {requirement.Id}: {ex.Message}" });
            }

            var deterministicChangeSet = await TryBuildDeterministicTriggerHandlerChangeSetAsync(
                repoPath,
                requirement,
                fileContents,
                testGuardMessages.Append($"Initial surgical edit failed: {ex.Message}"));
            if (deterministicChangeSet is not null)
            {
                return deterministicChangeSet;
            }

            Report("Surgical edit block was not unique enough. Asking AI to repair it with more context...");
            var failedPlanJson = JsonSerializer.Serialize(plans, new JsonSerializerOptions { WriteIndented = true });
            var repairFileContents = GetFileContentsForPlans(fileContents, plans);
            var repairPrompt = $@"The previous surgical edit plan failed validation.

Validation error:
{ex.Message}

Return a corrected JSON array only. Your entire response must start with '[' and end with ']'.
Use a larger Search block copied exactly from the relevant file so it appears only once.
Do not use empty Search values.
If the failed file is an Apex TriggerHandler, repair by using the full relevant trigger event method as the Search block and by adding any new helper method using a uniquely named neighbouring method or class-section anchor. Do not search for bare braces or repeated method calls.

Failed JSON plan to repair:
{failedPlanJson}

Original requirement:
{requirement.Description}

Relevant files from the failed plan:
{string.Join("\n\n", repairFileContents.Select(kvp => $"--- {kvp.Key} ---\n{kvp.Value}"))}";

            var repairResult = await RequestSurgicalEditPlansAsync(systemPrompt, repairPrompt, requirement.Id + "-REPAIR");
            if (!repairResult.IsSuccess)
            {
                return new FileChangeSet(
                    "Code implementation plan",
                    new List<FileChangeProposal>(),
                    repairResult.Messages.Append($"Initial surgical edit failed: {ex.Message}").ToList());
            }

            try
            {
                Report("AI repaired surgical targets: " + FormatEditPlanSummary(repairResult.Plans));
                Report("Validating repaired surgical edit plan...");
                var repairedTestGuardMessages = new List<string>();
                var repairedPlans = await EnsureApexTestPlansAsync(repoPath, requirement, fileContents, systemPrompt, repairResult.Plans, repairedTestGuardMessages);
                Report("Final repaired surgical targets after test guard: " + FormatEditPlanSummary(repairedPlans));
                return WithMessages(await BuildSurgicalChangeSetAsync(repoPath, repairedPlans), repairedTestGuardMessages);
            }
            catch (Exception repairEx)
            {
                return new FileChangeSet("Code implementation plan", new List<FileChangeProposal>(), new[]
                {
                    $"Surgical edit failed for requirement {requirement.Id}: {ex.Message}",
                    $"Repair attempt also failed: {repairEx.Message}"
                });
            }
        }
    }

    private void Report(string message)
    {
        _progress?.Invoke(message);
    }

    private static string FirstNonBlank(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static List<string> ResolveSuggestedFiles(string repoPath, SalesforceConfigRequirement requirement)
    {
        return requirement.SuggestedFiles
            .Select(NormalizeRelativePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(path => File.Exists(Path.Combine(repoPath, path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildImplementationHintText(SalesforceConfigRequirement requirement)
    {
        var hints = new List<string>();

        if (requirement.SuggestedFiles.Count > 0)
        {
            hints.Add("Suggested files: " + string.Join(", ", requirement.SuggestedFiles.Select(NormalizeRelativePath)));
        }

        if (!string.IsNullOrWhiteSpace(requirement.SuggestedTriggerEvent))
        {
            hints.Add("Suggested trigger event: " + requirement.SuggestedTriggerEvent.Trim());
        }

        if (!string.IsNullOrWhiteSpace(requirement.SuggestedHelperMethodName))
        {
            hints.Add("Suggested helper method: " + requirement.SuggestedHelperMethodName.Trim());
        }

        if (!string.IsNullOrWhiteSpace(requirement.ImplementationStrategy))
        {
            hints.Add("Suggested strategy: " + requirement.ImplementationStrategy.Trim());
        }

        if (!string.IsNullOrWhiteSpace(requirement.ImplementationKind))
        {
            hints.Add("Implementation kind: " + requirement.ImplementationKind.Trim());
        }

        if (!string.IsNullOrWhiteSpace(requirement.EventInvocation))
        {
            hints.Add("Event invocation: " + requirement.EventInvocation.Trim());
        }

        if (!string.IsNullOrWhiteSpace(requirement.HelperMethodCode))
        {
            hints.Add("Helper method code is available for deterministic placement.");
        }

        if (!string.IsNullOrWhiteSpace(requirement.TestMethodCode))
        {
            hints.Add("Test method code is available for deterministic placement.");
        }

        return hints.Count == 0 ? "None" : string.Join(Environment.NewLine, hints);
    }

    private static string FormatFileListForLog(IEnumerable<string> files)
    {
        var allFiles = files.ToList();
        var fileList = allFiles
            .Select(Path.GetFileName)
            .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
            .Take(8)
            .ToList();

        var suffix = allFiles.Count > fileList.Count ? $" (+{allFiles.Count - fileList.Count} more)" : string.Empty;
        return string.Join(", ", fileList) + suffix;
    }

    private static Dictionary<string, string> GetFileContentsForPlans(
        Dictionary<string, string> fileContents,
        IEnumerable<FileEditPlan> plans)
    {
        var planPaths = plans
            .Select(plan => NormalizeRelativePath(plan.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var selected = fileContents
            .Where(kvp => planPaths.Contains(NormalizeRelativePath(kvp.Key)))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        return selected.Count > 0 ? selected : fileContents;
    }

    private static string FormatEditPlanSummary(IEnumerable<FileEditPlan> plans)
    {
        var planList = plans.ToList();
        if (planList.Count == 0)
        {
            return "none";
        }

        var summary = planList
            .Take(8)
            .Select(plan => $"{Path.GetFileName(NormalizeRelativePath(plan.Path))} ({plan.Edits.Count} edit{(plan.Edits.Count == 1 ? string.Empty : "s")})")
            .ToList();

        var suffix = planList.Count > summary.Count ? $" (+{planList.Count - summary.Count} more file plan{(planList.Count - summary.Count == 1 ? string.Empty : "s")})" : string.Empty;
        return string.Join(", ", summary) + suffix;
    }

    private async Task<FileChangeSet?> TryBuildDeterministicTriggerHandlerChangeSetAsync(
        string repoPath,
        SalesforceConfigRequirement requirement,
        Dictionary<string, string> fileContents,
        IEnumerable<string> priorMessages)
    {
        if (!IsTriggerHandlerFragmentCandidate(requirement))
        {
            Report("Deterministic TriggerHandler placement skipped: requirement does not include enough trigger-handler intent.");
            return null;
        }

        var handlerPath = FindSuggestedPath(requirement, path =>
            path.EndsWith("TriggerHandler.cls", StringComparison.OrdinalIgnoreCase)
            && !IsApexTestPath(path));
        if (string.IsNullOrWhiteSpace(handlerPath))
        {
            Report("Deterministic TriggerHandler placement skipped: no TriggerHandler file was suggested or resolved.");
            return null;
        }

        handlerPath = NormalizeRelativePath(handlerPath);
        var handlerContent = await ReadContentFromCacheOrDiskAsync(repoPath, fileContents, handlerPath);
        if (string.IsNullOrWhiteSpace(handlerContent))
        {
            Report($"Deterministic TriggerHandler placement skipped: {Path.GetFileName(handlerPath)} could not be read.");
            return null;
        }

        var eventName = FirstNonBlank(requirement.SuggestedTriggerEvent, "beforeInsert");
        var helperMethodName = FirstNonBlank(requirement.SuggestedHelperMethodName, BuildHelperMethodName(requirement.FieldApiName));
        var defaultValue = ExtractDefaultAssignmentValue(requirement);
        var eventInvocation = FirstNonBlank(
            requirement.EventInvocation,
            string.IsNullOrWhiteSpace(defaultValue) ? string.Empty : $"{helperMethodName}((List<{requirement.ObjectApiName}>) newList);");
        var helperMethodCode = FirstNonBlank(
            requirement.HelperMethodCode,
            string.IsNullOrWhiteSpace(defaultValue)
                ? string.Empty
                : BuildDefaultHelperMethodCode(requirement.ObjectApiName, requirement.FieldApiName, helperMethodName, defaultValue));
        if (string.IsNullOrWhiteSpace(eventInvocation)
            || string.IsNullOrWhiteSpace(helperMethodCode)
            || ContainsPlaceholderCode(eventInvocation)
            || ContainsPlaceholderCode(helperMethodCode))
        {
            Report("Deterministic TriggerHandler placement skipped: missing valid invocation/helper code. Re-analyze coverage if this is a cached story.");
            return null;
        }

        string proposedHandler;
        try
        {
            Report($"Using deterministic TriggerHandler placement in {Path.GetFileName(handlerPath)}...");
            proposedHandler = InsertInvocationIntoTriggerEvent(handlerContent, eventName, eventInvocation);
            proposedHandler = InsertMethodBeforeFinalClassBrace(
                proposedHandler,
                helperMethodCode,
                helperMethodName);
        }
        catch (InvalidOperationException ex)
        {
            Report($"Deterministic TriggerHandler placement skipped: {ex.Message}");
            return null;
        }

        var proposals = new List<FileChangeProposal>
        {
            new(handlerPath, handlerContent, proposedHandler, true)
        };

        var testPath = FindSuggestedPath(requirement, IsApexTestPath);
        var testMethodName = FirstNonBlank(requirement.TestMethodName, BuildDefaultTestMethodName(requirement.FieldApiName));
        var testMethodCode = FirstNonBlank(
            requirement.TestMethodCode,
            string.IsNullOrWhiteSpace(defaultValue)
                ? string.Empty
                : BuildDefaultTestMethodCode(requirement.ObjectApiName, requirement.FieldApiName, testMethodName, defaultValue));
        if (!string.IsNullOrWhiteSpace(testPath)
            && !string.IsNullOrWhiteSpace(testMethodCode)
            && !ContainsPlaceholderCode(testMethodCode))
        {
            testPath = NormalizeRelativePath(testPath);
            var testContent = await ReadContentFromCacheOrDiskAsync(repoPath, fileContents, testPath);
            if (!string.IsNullOrWhiteSpace(testContent))
            {
                Report($"Using deterministic Apex test placement in {Path.GetFileName(testPath)}...");
                var proposedTest = InsertMethodBeforeFinalClassBrace(
                    testContent,
                    testMethodCode.Trim(),
                    testMethodName);
                proposals.Add(new FileChangeProposal(testPath, testContent, proposedTest, true));
            }
        }

        var messages = priorMessages
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Append("Used deterministic TriggerHandler placement because the AI surgical-edit JSON was unavailable.")
            .ToList();

        return new FileChangeSet("Deterministic Apex trigger-handler changes", proposals, messages);
    }

    private static bool IsTriggerHandlerFragmentCandidate(SalesforceConfigRequirement requirement)
    {
        return string.Equals(requirement.ImplementationKind, "trigger_handler", StringComparison.OrdinalIgnoreCase)
               || string.Equals(requirement.ImplementationKind, "apex_trigger_handler", StringComparison.OrdinalIgnoreCase)
               || (!string.IsNullOrWhiteSpace(requirement.SuggestedTriggerEvent)
                   && !string.IsNullOrWhiteSpace(requirement.EventInvocation)
                   && !string.IsNullOrWhiteSpace(requirement.HelperMethodCode));
    }

    private static bool ContainsPlaceholderCode(string value)
    {
        return value.Contains("...", StringComparison.Ordinal)
               || value.Contains("TODO", StringComparison.OrdinalIgnoreCase);
    }

    private static string FindSuggestedPath(SalesforceConfigRequirement requirement, Func<string, bool> predicate)
    {
        return requirement.SuggestedFiles
            .Select(NormalizeRelativePath)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && predicate(path)) ?? string.Empty;
    }

    private static async Task<string> ReadContentFromCacheOrDiskAsync(
        string repoPath,
        Dictionary<string, string> fileContents,
        string relativePath)
    {
        var cached = fileContents.FirstOrDefault(kvp => PathsEqual(kvp.Key, relativePath));
        return !string.IsNullOrEmpty(cached.Value)
            ? cached.Value
            : await ReadExistingContentAsync(repoPath, relativePath);
    }

    private static string InsertInvocationIntoTriggerEvent(string content, string eventName, string invocation)
    {
        var lineEnding = content.Contains("\r\n") ? "\r\n" : "\n";
        var normalized = content.Replace("\r\n", "\n");
        var match = Regex.Match(
            normalized,
            $@"(?m)^(?<indent>\s*)public\s+override\s+void\s+{Regex.Escape(eventName)}\s*\(\s*\)\s*\{{");
        if (!match.Success)
        {
            throw new InvalidOperationException($"TriggerHandler event method was not found: {eventName}");
        }

        var openingBrace = normalized.IndexOf('{', match.Index + match.Length - 1);
        var closingBrace = FindMatchingBrace(normalized, openingBrace);
        if (closingBrace < 0)
        {
            throw new InvalidOperationException($"Could not find closing brace for TriggerHandler event method: {eventName}");
        }

        var methodBody = normalized[match.Index..(closingBrace + 1)];
        if (methodBody.Contains(invocation.Trim(), StringComparison.Ordinal))
        {
            return content;
        }

        var methodIndent = match.Groups["indent"].Value;
        var invocationIndent = methodIndent + "    ";
        var insertion = invocationIndent + invocation.Trim().TrimEnd(';') + ";" + "\n";
        var insertAt = GetLineStart(normalized, closingBrace);
        var proposed = normalized.Insert(insertAt, insertion);
        return lineEnding == "\r\n" ? proposed.Replace("\n", "\r\n") : proposed;
    }

    private static string InsertMethodBeforeFinalClassBrace(string content, string methodCode, string methodName)
    {
        if (ContainsMethodDeclaration(content, methodName))
        {
            return content;
        }

        var lineEnding = content.Contains("\r\n") ? "\r\n" : "\n";
        var normalized = content.Replace("\r\n", "\n");
        var finalBrace = normalized.LastIndexOf('}');
        if (finalBrace < 0)
        {
            throw new InvalidOperationException("Could not find final class brace for deterministic Apex insertion.");
        }

        var finalBraceIndent = GetLineIndent(normalized, finalBrace);
        var methodIndent = finalBraceIndent + "    ";
        var insertion = "\n\n" + IndentCodeBlock(methodCode.Trim(), methodIndent) + "\n";
        var proposed = normalized.Insert(GetLineStart(normalized, finalBrace), insertion);
        return lineEnding == "\r\n" ? proposed.Replace("\n", "\r\n") : proposed;
    }

    private static bool ContainsMethodDeclaration(string content, string methodName)
    {
        if (string.IsNullOrWhiteSpace(methodName))
        {
            return false;
        }

        return Regex.IsMatch(
            content,
            $@"(?m)^\s*(?:@\w+\s*)*(?:(?:public|private|protected|global|static|override|virtual|testMethod)\s+)+(?:[\w<>\[\],]+\s+)+{Regex.Escape(methodName)}\s*\(",
            RegexOptions.IgnoreCase);
    }

    private static int FindMatchingBrace(string content, int openingBrace)
    {
        if (openingBrace < 0 || openingBrace >= content.Length || content[openingBrace] != '{')
        {
            return -1;
        }

        var depth = 0;
        for (var i = openingBrace; i < content.Length; i++)
        {
            if (content[i] == '{')
            {
                depth++;
            }
            else if (content[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static string GetLineIndent(string content, int index)
    {
        var lineStart = GetLineStart(content, index);
        var cursor = lineStart;
        while (cursor < content.Length && (content[cursor] == ' ' || content[cursor] == '\t'))
        {
            cursor++;
        }

        return content[lineStart..cursor];
    }

    private static int GetLineStart(string content, int index)
    {
        var lineStart = content.LastIndexOf('\n', Math.Max(0, index - 1));
        return lineStart < 0 ? 0 : lineStart + 1;
    }

    private static string IndentCodeBlock(string code, string indent)
    {
        return string.Join("\n", code.Replace("\r\n", "\n").Split('\n').Select(line =>
            string.IsNullOrWhiteSpace(line) ? string.Empty : indent + line.TrimEnd()));
    }

    private static string BuildHelperMethodName(string fieldApiName)
    {
        if (string.IsNullOrWhiteSpace(fieldApiName))
        {
            return "applyApexAlternative";
        }

        var name = fieldApiName.EndsWith("__c", StringComparison.OrdinalIgnoreCase)
            ? fieldApiName[..^3]
            : fieldApiName;
        var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0
            ? "applyApexAlternative"
            : "default" + string.Concat(parts.Select(part =>
                char.ToUpperInvariant(part[0]) + (part.Length > 1 ? part[1..].ToLowerInvariant() : string.Empty)));
    }

    private static string BuildDefaultTestMethodName(string fieldApiName)
    {
        var helperName = BuildHelperMethodName(fieldApiName);
        return "test" + char.ToUpperInvariant(helperName[0]) + helperName[1..];
    }

    private static string BuildDefaultHelperMethodCode(
        string objectApiName,
        string fieldApiName,
        string helperMethodName,
        string defaultValue)
    {
        var variableName = BuildRecordVariableName(objectApiName);
        return $$"""
private void {{helperMethodName}}(List<{{objectApiName}}> records) {
    for ({{objectApiName}} {{variableName}} : records) {
        if (String.isBlank({{variableName}}.{{fieldApiName}})) {
            {{variableName}}.{{fieldApiName}} = '{{EscapeApexString(defaultValue)}}';
        }
    }
}
""";
    }

    private static string BuildDefaultTestMethodCode(
        string objectApiName,
        string fieldApiName,
        string testMethodName,
        string defaultValue)
    {
        var variableName = BuildRecordVariableName(objectApiName);
        var objectLabel = StripCustomObjectSuffix(objectApiName);
        return $$"""
@IsTest
static void {{testMethodName}}() {
    {{objectApiName}} {{variableName}} = new {{objectApiName}}(Name = 'Test {{objectLabel}}');
    insert {{variableName}};

    {{variableName}} = [SELECT {{fieldApiName}} FROM {{objectApiName}} WHERE Id = :{{variableName}}.Id];
    System.assertEquals('{{EscapeApexString(defaultValue)}}', {{variableName}}.{{fieldApiName}}, '{{fieldApiName}} should default on insert.');
}
""";
    }

    private static string BuildRecordVariableName(string objectApiName)
    {
        var baseName = StripCustomObjectSuffix(objectApiName);
        return string.IsNullOrWhiteSpace(baseName)
            ? "record"
            : char.ToLowerInvariant(baseName[0]) + baseName[1..] + "Record";
    }

    private static string StripCustomObjectSuffix(string objectApiName)
    {
        if (string.IsNullOrWhiteSpace(objectApiName))
        {
            return string.Empty;
        }

        return objectApiName.EndsWith("__c", StringComparison.OrdinalIgnoreCase)
            ? objectApiName[..^3]
            : objectApiName;
    }

    private static string ExtractDefaultAssignmentValue(SalesforceConfigRequirement requirement)
    {
        if (!string.IsNullOrWhiteSpace(requirement.DefaultValue))
        {
            return requirement.DefaultValue.Trim();
        }

        var combined = string.Join(Environment.NewLine, new[]
        {
            requirement.Description,
            requirement.Label,
            requirement.Operation
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        var bracketedValues = Regex.Matches(combined, @"\[(?<value>[^\]]+)\]")
            .Select(match => match.Groups["value"].Value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return bracketedValues.Count > 0 ? string.Join(";", bracketedValues) : string.Empty;
    }

    private static string EscapeApexString(string value)
    {
        return value.Replace("\\", "\\\\").Replace("'", "\\'");
    }

    private async Task<SurgicalPlanResult> RequestSurgicalEditPlansAsync(string systemPrompt, string userMessage, string requirementId)
    {
        var response = await _deepSeek.SendChatAsync(DeepSeekModels.Coding, systemPrompt, userMessage, 0.1, 4000);
        var firstBracket = response.IndexOf('[');
        var lastBracket = response.LastIndexOf(']');

        if (firstBracket < 0 || lastBracket < 0 || lastBracket <= firstBracket)
        {
            return SurgicalPlanResult.Failed($"AI failed to return a JSON array for requirement: {requirementId}");
        }

        var json = response.Substring(firstBracket, lastBracket - firstBracket + 1);

        try
        {
            var plans = JsonSerializer.Deserialize<List<FileEditPlan>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return plans is null || plans.Count == 0
                ? SurgicalPlanResult.Failed($"No edits proposed by AI for requirement: {requirementId}")
                : SurgicalPlanResult.Success(plans, json);
        }
        catch (Exception ex)
        {
            return SurgicalPlanResult.Failed($"JSON parse error for requirement {requirementId}: {ex.Message}");
        }
    }

    private static bool IsRepairableSurgicalEditFailure(Exception ex)
    {
        var message = ex.Message;
        return message.Contains("ambiguous", StringComparison.OrdinalIgnoreCase)
               || message.Contains("not found", StringComparison.OrdinalIgnoreCase)
               || message.Contains("empty search block", StringComparison.OrdinalIgnoreCase)
               || message.Contains("exact match", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<List<FileEditPlan>> EnsureApexTestPlansAsync(
        string repoPath,
        SalesforceConfigRequirement requirement,
        Dictionary<string, string> fileContents,
        string systemPrompt,
        List<FileEditPlan> plans,
        List<string> messages)
    {
        var productionApexPlans = plans
            .Where(plan => IsProductionApexPath(NormalizeRelativePath(plan.Path)) && plan.Edits.Count > 0)
            .ToList();

        if (productionApexPlans.Count == 0)
        {
            return plans;
        }

        var missingTestPlans = productionApexPlans
            .Where(plan => !HasMatchingTestPlan(plans, NormalizeRelativePath(plan.Path)))
            .ToList();

        if (missingTestPlans.Count == 0)
        {
            Report("Apex production edits include matching test edits.");
            return plans;
        }

        Report($"Apex edit detected without matching test edit. Looking for {missingTestPlans.Count} test target(s)...");
        var testFiles = await LoadMatchingTestFilesAsync(repoPath, fileContents, missingTestPlans.Select(plan => NormalizeRelativePath(plan.Path)));
        if (testFiles.Count == 0)
        {
            messages.Add("Apex production code changes were proposed, but no matching test class was found. Please add or identify the test class before applying.");
            return plans;
        }

        var missingProductionList = string.Join(Environment.NewLine, missingTestPlans.Select(plan => "- " + NormalizeRelativePath(plan.Path)));
        var existingPlanJson = JsonSerializer.Serialize(plans, new JsonSerializerOptions { WriteIndented = true });
        var testPrompt = $@"The previous surgical edit plan changes Apex production code but does not include matching test updates.

Add only surgical edits to the matching Apex test class files.
Do not repeat production file edits unless they are unchanged from the original plan.
Do not create new test files unless no existing test file is provided.
Return a JSON array only.

Requirement:
{requirement.Description}

Production Apex files missing test edits:
{missingProductionList}

Existing edit plan:
{existingPlanJson}

Available test files:
{string.Join("\n\n", testFiles.Select(kvp => $"--- {kvp.Key} ---\n{kvp.Value}"))}";

        Report("Asking AI to add matching Apex test edits...");
        var testPlanResult = await RequestSurgicalEditPlansAsync(systemPrompt, testPrompt, requirement.Id + "-TEST");
        if (!testPlanResult.IsSuccess)
        {
            messages.AddRange(testPlanResult.Messages.Select(message => "Apex test edit pass failed: " + message));
            return plans;
        }

        var testPlans = testPlanResult.Plans
            .Where(plan => IsApexTestPath(NormalizeRelativePath(plan.Path)) && plan.Edits.Count > 0)
            .ToList();

        if (testPlans.Count == 0)
        {
            messages.Add("Apex production code changes were proposed, but the AI did not return any matching test edits.");
            return plans;
        }

        messages.Add($"Apex test guard added {testPlans.Count} matching test file edit plan(s).");
        return MergeFilePlans(plans, testPlans);
    }

    private async Task<Dictionary<string, string>> LoadMatchingTestFilesAsync(
        string repoPath,
        Dictionary<string, string> fileContents,
        IEnumerable<string> productionPaths)
    {
        var testFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var productionPathList = productionPaths.ToList();

        foreach (var existingTest in fileContents.Where(kvp => IsApexTestPath(kvp.Key)))
        {
            if (productionPathList.Any(productionPath => IsLikelyTestForProduction(productionPath, existingTest.Key)))
            {
                testFiles[existingTest.Key] = existingTest.Value;
            }
        }

        foreach (var productionPath in productionPathList)
        {
            foreach (var testPath in FindMatchingTestPaths(repoPath, productionPath))
            {
                if (testFiles.ContainsKey(testPath))
                {
                    continue;
                }

                var content = await ReadExistingContentAsync(repoPath, testPath);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    testFiles[testPath] = content;
                    fileContents[testPath] = content;
                }
            }
        }

        return testFiles;
    }

    private static IEnumerable<string> FindMatchingTestPaths(string repoPath, string productionPath)
    {
        var productionName = Path.GetFileNameWithoutExtension(productionPath);
        if (string.IsNullOrWhiteSpace(productionName))
        {
            return Enumerable.Empty<string>();
        }

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var classesDirectory in FindClassesDirectories(repoPath))
        {
            foreach (var pattern in BuildTestFilePatterns(productionName))
            {
                foreach (var file in Directory.GetFiles(classesDirectory, pattern, SearchOption.TopDirectoryOnly))
                {
                    candidates.Add(Path.GetRelativePath(repoPath, file));
                }
            }
        }

        return candidates.OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> FindClassesDirectories(string repoPath)
    {
        foreach (var directory in Directory.GetDirectories(repoPath, "classes", SearchOption.AllDirectories))
        {
            if (directory.Contains($"{Path.DirectorySeparatorChar}.sfdx{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || directory.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || directory.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return directory;
        }
    }

    private static IEnumerable<string> BuildTestFilePatterns(string productionName)
    {
        yield return productionName + "Test.cls";
        yield return productionName + "*Test*.cls";

        if (productionName.EndsWith("Trigger", StringComparison.OrdinalIgnoreCase))
        {
            var objectName = productionName[..^"Trigger".Length];
            yield return objectName + "TriggerHandlerTest.cls";
            yield return objectName + "*Test*.cls";
        }
    }

    private static List<FileEditPlan> MergeFilePlans(List<FileEditPlan> originalPlans, List<FileEditPlan> additionalPlans)
    {
        var merged = originalPlans.ToList();
        foreach (var additionalPlan in additionalPlans)
        {
            var existing = merged.FirstOrDefault(plan => PathsEqual(plan.Path, additionalPlan.Path));
            if (existing is null)
            {
                merged.Add(additionalPlan);
                continue;
            }

            existing.Edits.AddRange(additionalPlan.Edits);
        }

        return merged;
    }

    private static bool HasMatchingTestPlan(IEnumerable<FileEditPlan> plans, string productionPath)
    {
        return plans.Any(plan => IsApexTestPath(NormalizeRelativePath(plan.Path))
                                 && plan.Edits.Count > 0
                                 && IsLikelyTestForProduction(productionPath, NormalizeRelativePath(plan.Path)));
    }

    private static bool IsLikelyTestForProduction(string productionPath, string testPath)
    {
        var productionName = Path.GetFileNameWithoutExtension(productionPath);
        var testName = Path.GetFileNameWithoutExtension(testPath);
        if (string.IsNullOrWhiteSpace(productionName) || string.IsNullOrWhiteSpace(testName))
        {
            return false;
        }

        if (!testName.Contains("Test", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (testName.Contains(productionName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (productionName.EndsWith("Trigger", StringComparison.OrdinalIgnoreCase))
        {
            var objectName = productionName[..^"Trigger".Length];
            return testName.Contains(objectName, StringComparison.OrdinalIgnoreCase)
                   && testName.Contains("TriggerHandler", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsProductionApexPath(string path)
    {
        return (path.EndsWith(".cls", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".trigger", StringComparison.OrdinalIgnoreCase))
               && !IsApexTestPath(path);
    }

    private static bool IsApexTestPath(string path)
    {
        return path.EndsWith(".cls", StringComparison.OrdinalIgnoreCase)
               && Path.GetFileNameWithoutExtension(path).Contains("Test", StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(NormalizeRelativePath(left), NormalizeRelativePath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static FileChangeSet WithMessages(FileChangeSet changeSet, IEnumerable<string> messages)
    {
        var finalMessages = new List<string>();
        if (changeSet.Messages is not null)
        {
            finalMessages.AddRange(changeSet.Messages);
        }

        finalMessages.AddRange(messages.Where(message => !string.IsNullOrWhiteSpace(message)));
        return finalMessages.Count == 0
            ? changeSet
            : new FileChangeSet(changeSet.Title, changeSet.Files, finalMessages);
    }

    private static string BuildSurgicalEditSystemPrompt()
    {
        return @"You are a Salesforce Developer.
Given a requirement and the contents of relevant files, propose surgical edits (search and replace blocks) to implement the requirement.
Rules:
1. Use EXACT search blocks copied from the provided file content.
2. Return ONLY a JSON array of FileEditPlan objects. No markdown, no explanation, no prose.
3. Each FileEditPlan must have a 'Path' and a list of 'Edits'.
4. Each CodeEdit must have 'Search' and 'Replace' properties.
5. Do NOT rewrite the whole file. Use minimal search/replace blocks.
6. If a trigger only delegates to a TriggerHandler, do not edit the trigger shell unless the trigger events themselves must change. Prefer the TriggerHandler or existing service/helper class.
7. Never return an empty Search value. If you cannot find an exact block to replace, return [].
8. Search must be unique in the target file. Include the method signature, surrounding if/loop block, or nearby comments so the Search block appears only once.
9. Do not search for a single common line such as an isolated brace, return statement, method call, or variable declaration.
10. If you change Apex production code (.cls or .trigger that is not a test), also update the matching Apex test class in the same JSON plan when a test class is provided.
11. Prefer existing test classes such as MyClassTest.cls or MyTriggerHandlerTest.cls. Do not invent unrelated test files.
12. For Apex classes that extend TriggerHandler, prefer this project pattern for new trigger behavior: add a focused private helper method and call it from the correct event override such as beforeInsert, beforeUpdate, afterInsert, or afterUpdate.
13. When adding a call inside a TriggerHandler event override, the Search block must include the entire override method body copied exactly from the file, not just the inserted call location.
14. When adding a helper method to a TriggerHandler, anchor the insertion with a uniquely named neighbouring method or an existing class-section block. Never anchor helper insertion with only a closing brace.
15. For normal Apex classes, prefer the existing class/service/helper pattern. Do not invent a trigger if the provided context points to an existing class or service.
16. For LWC changes, edit only the relevant .js/.html/.css files provided. Keep HTML/template syntax valid and preserve existing public API names, wire adapters, imports, and component style conventions.
17. For Aura and Visualforce changes, preserve framework syntax and edit only the smallest necessary component/controller/markup block.
18. Your entire response must start with '[' and end with ']'. If you cannot safely edit, return [].

Example Output:
[
  {
    ""Path"": ""force-app/main/default/classes/MyClass.cls"",
    ""Edits"": [
      {
        ""Search"": ""public void myMethod() {\n    // existing code\n}"",
        ""Replace"": ""public void myMethod() {\n    // new code\n}""
      }
    ]
  }
]";
    }

    private sealed class SurgicalPlanResult
    {
        public bool IsSuccess { get; init; }
        public List<FileEditPlan> Plans { get; init; } = new();
        public List<string> Messages { get; init; } = new();
        public string RawJson { get; init; } = string.Empty;

        public static SurgicalPlanResult Success(List<FileEditPlan> plans, string rawJson)
        {
            return new SurgicalPlanResult { IsSuccess = true, Plans = plans, RawJson = rawJson };
        }

        public static SurgicalPlanResult Failed(string message)
        {
            return new SurgicalPlanResult { Messages = new List<string> { message } };
        }
    }

    public async Task<FileChangeSet> BuildDirectWriteChangeSetAsync(string repoPath, string relativePath, string content)
    {
        return await BuildDirectWriteChangeSetAsync(repoPath, new[] { new RequestedFileWrite(relativePath, content) });
    }

    public async Task<FileChangeSet> BuildDirectWriteChangeSetAsync(string repoPath, IEnumerable<RequestedFileWrite> requestedWrites)
    {
        var proposals = new List<FileChangeProposal>();

        foreach (var requestedWrite in requestedWrites)
        {
            var normalizedPath = NormalizeRelativePath(requestedWrite.RelativePath);
            var existingContent = await ReadExistingContentAsync(repoPath, normalizedPath);

            if (!string.IsNullOrEmpty(existingContent) && IsSensitiveCodeFile(normalizedPath))
            {
                throw new InvalidOperationException($"Full-file overwrite is blocked for existing code file: {normalizedPath}. Use surgical search/replace instead.");
            }

            var proposedContent = PrepareProposedContent(normalizedPath, existingContent, requestedWrite.Content);

            proposals.Add(new FileChangeProposal(
                normalizedPath,
                existingContent,
                proposedContent,
                !string.IsNullOrEmpty(existingContent)));
        }

        return new FileChangeSet("AI proposed file changes", proposals);
    }

    private static bool IsSensitiveCodeFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".cls" or ".trigger" or ".js" or ".html" or ".css" or ".cmp" or ".page";
    }

    public async Task<FileChangeSet> BuildGeneratedCodeChangeSetAsync(string repoPath, string code, string command)
    {
        var proposals = new List<FileChangeProposal>();
        var (fileName, relativePath) = GetTargetFileDetails(code, command);
        var normalizedPath = NormalizeRelativePath(relativePath);
        var existingContent = await ReadExistingContentAsync(repoPath, normalizedPath);

        if (!string.IsNullOrEmpty(existingContent) && IsSensitiveCodeFile(normalizedPath))
        {
            throw new InvalidOperationException($"Generation fallback blocked: File '{normalizedPath}' already exists. Use surgical search/replace to modify existing code.");
        }

        proposals.Add(new FileChangeProposal(normalizedPath, existingContent, code, !string.IsNullOrEmpty(existingContent)));

        if (fileName.EndsWith(".cls", StringComparison.OrdinalIgnoreCase))
        {
            var metaPath = normalizedPath.Replace(".cls", "-meta.xml", StringComparison.OrdinalIgnoreCase);
            var existingMeta = await ReadExistingContentAsync(repoPath, metaPath);
            proposals.Add(new FileChangeProposal(metaPath, existingMeta, BuildApexMetaXml(), !string.IsNullOrEmpty(existingMeta)));
        }

        return new FileChangeSet("Generated code changes", proposals);
    }

    public async Task<FileChangeSet> BuildSurgicalChangeSetAsync(string repoPath, List<FileEditPlan> filePlans)
    {
        var proposals = new List<FileChangeProposal>();

        foreach (var filePlan in filePlans)
        {
            var normalizedPath = NormalizeRelativePath(filePlan.Path);
            var existingContent = await ReadExistingContentAsync(repoPath, normalizedPath);
            
            if (string.IsNullOrEmpty(existingContent))
            {
                throw new FileNotFoundException($"Cannot perform surgical edit on missing file: {normalizedPath}");
            }

            var proposedContent = existingContent;
            foreach (var edit in filePlan.Edits)
            {
                proposedContent = ApplySurgicalEdit(normalizedPath, proposedContent, edit);
            }

            proposals.Add(new FileChangeProposal(
                normalizedPath,
                existingContent,
                proposedContent,
                true));
        }

        return new FileChangeSet("AI proposed surgical code changes", proposals);
    }

    private static string ApplySurgicalEdit(string path, string content, CodeEdit edit)
    {
        var search = edit.Search.Trim();
        var replace = edit.Replace.Trim();

        if (string.IsNullOrWhiteSpace(search))
        {
            // If search is empty, this might be an append/prepend instruction, 
            // but for now we require explicit blocks for safety.
            throw new InvalidOperationException($"Empty search block provided for {path}. Surgical edits require an exact match.");
        }

        // We use a simple string search to ensure exactness. 
        // AI often adds/removes surrounding whitespace, so we normalize line endings and trim.
        var normalizedContent = content.Replace("\r\n", "\n");
        var normalizedSearch = search.Replace("\r\n", "\n");
        var normalizedReplace = replace.Replace("\r\n", "\n");

        var index = normalizedContent.IndexOf(normalizedSearch, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidOperationException($"Search block not found in {path}. Check for exact matching including indentation.");
        }

        if (normalizedContent.IndexOf(normalizedSearch, index + 1, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException($"Search block is ambiguous in {path} (found multiple occurrences). Provide more context in the search block.");
        }

        var newContent = normalizedContent.Remove(index, normalizedSearch.Length).Insert(index, normalizedReplace);
        
        // Restore original line endings if they were CRLF
        return content.Contains("\r\n") ? newContent.Replace("\n", "\r\n") : newContent;
    }

    public async Task ApplyChangeSetAsync(string repoPath, FileChangeSet changeSet)
    {
        foreach (var proposal in changeSet.Files)
        {
            var fullPath = Path.Combine(repoPath, proposal.RelativePath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(fullPath, proposal.ProposedContent);
        }
    }

    public string BuildDiffPreview(FileChangeSet changeSet)
    {
        var builder = new StringBuilder();
        builder.AppendLine(changeSet.Title);

        if (changeSet.Messages != null && changeSet.Messages.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("MESSAGES / WARNINGS:");
            foreach (var msg in changeSet.Messages)
            {
                builder.AppendLine($"* {msg}");
            }
        }

        builder.AppendLine(new string('=', 80));

        foreach (var file in changeSet.Files)
        {
            builder.AppendLine();
            builder.AppendLine($"File: {file.RelativePath}");
            builder.AppendLine(file.FileExists ? "Operation: Update" : "Operation: Create");
            builder.AppendLine(new string('-', 80));
            builder.Append(BuildFileDiff(file));
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string PrepareProposedContent(string relativePath, string existingContent, string proposedContent)
    {
        if (!IsMetadataMergeTarget(relativePath))
        {
            return proposedContent;
        }

        if (string.IsNullOrWhiteSpace(existingContent))
        {
            throw new InvalidOperationException("Rejected profile metadata write. Profile and permission set updates must target existing files.");
        }

        var fieldPermissionSnippet = ExtractFieldPermissionSnippet(proposedContent);
        return MergeFieldPermissions(existingContent, fieldPermissionSnippet);
    }


    private static bool IsMetadataMergeTarget(string relativePath)
    {
        return relativePath.EndsWith(".profile-meta.xml", StringComparison.OrdinalIgnoreCase)
               || relativePath.EndsWith(".permissionset-meta.xml", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractFieldPermissionSnippet(string content)
    {
        var fieldPermissionPattern = @"<fieldPermissions>.*?</fieldPermissions>";
        var matches = Regex.Matches(content, fieldPermissionPattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (matches.Count == 0)
        {
            throw new InvalidOperationException("Rejected malformed profile metadata response. No valid fieldPermissions block was found.");
        }

        return string.Join(Environment.NewLine, matches.Cast<Match>().Select(match => match.Value.Trim()));
    }

    private static string MergeFieldPermissions(string existingContent, string snippetContent)
    {
        var fieldPermissionPattern = @"<fieldPermissions>.*?</fieldPermissions>";
        var newBlockMatches = Regex.Matches(snippetContent, fieldPermissionPattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (newBlockMatches.Count == 0)
        {
            return existingContent;
        }

        var allBlocks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var existingMatches = Regex.Matches(existingContent, fieldPermissionPattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);

        foreach (Match match in existingMatches)
        {
            var fieldName = ExtractFieldName(match.Value);
            if (!string.IsNullOrWhiteSpace(fieldName))
            {
                allBlocks[fieldName] = NormalizeBlock(match.Value);
            }
        }

        foreach (Match match in newBlockMatches)
        {
            var fieldName = ExtractFieldName(match.Value);
            if (!string.IsNullOrWhiteSpace(fieldName))
            {
                allBlocks[fieldName] = NormalizeBlock(match.Value);
            }
        }

        if (allBlocks.Count == 0)
        {
            return existingContent;
        }

        var indent = DetectIndent(existingContent, existingMatches.Count > 0 ? existingMatches[0].Index : -1);
        var orderedBlocks = allBlocks
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => IndentBlock(kvp.Value, indent));
        var blockText = string.Join(Environment.NewLine, orderedBlocks);

        if (existingMatches.Count > 0)
        {
            var firstIndex = existingMatches[0].Index;
            var lastMatch = existingMatches[^1];
            var endIndex = lastMatch.Index + lastMatch.Length;
            return existingContent.Substring(0, firstIndex) + blockText + existingContent.Substring(endIndex);
        }

        var closingTag = existingContent.Contains("</Profile>", StringComparison.OrdinalIgnoreCase)
            ? "</Profile>"
            : "</PermissionSet>";
        var closingIndex = existingContent.LastIndexOf(closingTag, StringComparison.OrdinalIgnoreCase);
        if (closingIndex < 0)
        {
            return existingContent;
        }

        return existingContent.Insert(closingIndex, blockText + Environment.NewLine);
    }

    private static string ExtractFieldName(string block)
    {
        var match = Regex.Match(block, @"<field>(.*?)</field>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    private static string NormalizeBlock(string block)
    {
        return block.Trim();
    }

    private static string DetectIndent(string content, int index)
    {
        if (index <= 0 || index > content.Length)
        {
            return "    ";
        }

        var lineStart = content.LastIndexOf('\n', index);
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        var indentLength = 0;

        while (lineStart + indentLength < content.Length)
        {
            var ch = content[lineStart + indentLength];
            if (ch != ' ' && ch != '\t')
            {
                break;
            }

            indentLength++;
        }

        return indentLength > 0 ? content.Substring(lineStart, indentLength) : "    ";
    }

    private static string IndentBlock(string block, string indent)
    {
        var lines = block.Replace("\r\n", "\n").Split('\n');
        return string.Join(Environment.NewLine, lines.Select(line => indent + line.Trim()));
    }

    private static string BuildFileDiff(FileChangeProposal proposal)
    {
        var oldLines = SplitLines(proposal.ExistingContent);
        var newLines = SplitLines(proposal.ProposedContent);
        var builder = new StringBuilder();
        builder.AppendLine($"--- {proposal.RelativePath} (current)");
        builder.AppendLine($"+++ {proposal.RelativePath} (proposed)");

        var contextLines = IsMetadataMergeTarget(proposal.RelativePath) ? 0 : 3;
        var prefixLength = 0;
        while (prefixLength < oldLines.Length
               && prefixLength < newLines.Length
               && oldLines[prefixLength] == newLines[prefixLength])
        {
            prefixLength++;
        }

        var suffixLength = 0;
        while (suffixLength < oldLines.Length - prefixLength
               && suffixLength < newLines.Length - prefixLength
               && oldLines[oldLines.Length - 1 - suffixLength] == newLines[newLines.Length - 1 - suffixLength])
        {
            suffixLength++;
        }

        var oldChangeEnd = oldLines.Length - suffixLength;
        var newChangeEnd = newLines.Length - suffixLength;
        var contextStart = Math.Max(0, prefixLength - contextLines);

        if (contextStart > 0)
        {
            builder.AppendLine("  ...");
        }

        for (var i = contextStart; i < prefixLength; i++)
        {
            builder.AppendLine($"  {oldLines[i]}");
        }

        for (var i = prefixLength; i < oldChangeEnd; i++)
        {
            builder.AppendLine($"- {oldLines[i]}");
        }

        for (var i = prefixLength; i < newChangeEnd; i++)
        {
            builder.AppendLine($"+ {newLines[i]}");
        }

        var trailingContext = Math.Min(contextLines, suffixLength);
        for (var i = 0; i < trailingContext; i++)
        {
            builder.AppendLine($"  {newLines[newChangeEnd + i]}");
        }

        if (suffixLength > contextLines)
        {
            builder.AppendLine("  ...");
        }

        return builder.ToString();
    }

    private static string[] SplitLines(string content)
    {
        return string.IsNullOrEmpty(content)
            ? Array.Empty<string>()
            : content.Replace("\r\n", "\n").Split('\n');
    }

    private static async Task<string> ReadExistingContentAsync(string repoPath, string relativePath)
    {
        var fullPath = Path.Combine(repoPath, relativePath);
        return File.Exists(fullPath) ? await File.ReadAllTextAsync(fullPath) : string.Empty;
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        return relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
    }

    private static (string FileName, string RelativePath) GetTargetFileDetails(string code, string command)
    {
        if (command.Contains("trigger", StringComparison.OrdinalIgnoreCase))
        {
            var objectName = "Account";
            var match = Regex.Match(command, @"(?:on|for)\s+(\w+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                objectName = match.Groups[1].Value;
            }

            var triggerName = $"{objectName}Trigger.trigger";
            return (triggerName, Path.Combine("force-app", "main", "default", "triggers", triggerName));
        }

        var classMatch = Regex.Match(code, @"class\s+(\w+)");
        var className = classMatch.Success ? classMatch.Groups[1].Value : "NewClass";
        var fileName = $"{className}.cls";
        return (fileName, Path.Combine("force-app", "main", "default", "classes", fileName));
    }

    private static string BuildApexMetaXml()
    {
        return @"<?xml version=""1.0"" encoding=""UTF-8""?>
<ApexClass xmlns=""http://soap.sforce.com/2006/04/metadata"">
    <apiVersion>66.0</apiVersion>
    <status>Active</status>
</ApexClass>";
    }
}

public sealed record RequestedFileWrite(string RelativePath, string Content);

public sealed record FileChangeProposal(string RelativePath, string ExistingContent, string ProposedContent, bool FileExists);

public sealed record FileChangeSet(string Title, IReadOnlyList<FileChangeProposal> Files, IReadOnlyList<string>? Messages = null);






