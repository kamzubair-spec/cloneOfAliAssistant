using System.Text.Json;
using System.Text.RegularExpressions;
using eZBERP_AI_IDE.Models;

namespace eZBERP_AI_IDE.Services;

public sealed class AlternativeImplementationService
{
    private readonly DeepSeekClient _deepSeekClient;

    public AlternativeImplementationService(DeepSeekClient deepSeekClient)
    {
        _deepSeekClient = deepSeekClient;
    }

    public async Task<AlternativeImplementation?> BuildAlternativeAsync(SalesforceConfigRequirement requirement, string unsupportedReason)
    {
        if (!IsFlowAutomationCandidate(requirement))
        {
            return null;
        }

        if (IsBackgroundRecordCreateDefaultCandidate(requirement))
        {
            return BuildApexAlternative(requirement, BuildBackgroundDefaultDecision(requirement));
        }

        var decision = await AskModelForAlternativeAsync(requirement, unsupportedReason);
        if (decision is null || !decision.HasAlternative || !IsApexAlternative(decision.AlternativeType))
        {
            return null;
        }

        if (decision.Confidence < 0.55m)
        {
            return null;
        }

        return BuildApexAlternative(requirement, decision);
    }

    private static bool IsFlowAutomationCandidate(SalesforceConfigRequirement requirement)
    {
        if (!string.Equals(requirement.Type, "flow", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var combined = string.Join(" ", new[]
        {
            requirement.Description,
            requirement.Label,
            requirement.Operation,
            requirement.ObjectApiName,
            requirement.FieldApiName,
            requirement.DefaultValue
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        if (string.IsNullOrWhiteSpace(combined))
        {
            return false;
        }

        var describesAutomationOutcome = ContainsAny(
            combined,
            "default",
            "set",
            "populate",
            "update",
            "assign",
            "when created",
            "on create",
            "on insert",
            "before save",
            "record-triggered");

        var hasTargetContext =
            !string.IsNullOrWhiteSpace(requirement.ObjectApiName) ||
            !string.IsNullOrWhiteSpace(requirement.FieldApiName);

        return describesAutomationOutcome && hasTargetContext;
    }

    private static bool IsBackgroundRecordCreateDefaultCandidate(SalesforceConfigRequirement requirement)
    {
        var combined = string.Join(" ", new[]
        {
            requirement.Description,
            requirement.Label,
            requirement.Operation,
            requirement.ObjectApiName,
            requirement.FieldApiName,
            requirement.DefaultValue
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        var isCreateDefaulting =
            ContainsAny(combined, "default", "populate", "set", "assign", "initialise", "initialize")
            && ContainsAny(combined, "created", "create", "creates", "on create", "record create", "record creation", "before insert");

        var isBackgroundOnly =
            ContainsAny(combined, "background", "not appear", "not visible", "hidden", "not displayed", "not editable")
            || !ContainsAny(combined, "screen flow", "user selects", "user enters", "manual choice", "flow navigation", "approval");

        return isCreateDefaulting
               && isBackgroundOnly
               && !string.IsNullOrWhiteSpace(requirement.ObjectApiName)
               && !string.IsNullOrWhiteSpace(requirement.FieldApiName);
    }

    private static AlternativeImplementationDecision BuildBackgroundDefaultDecision(SalesforceConfigRequirement requirement)
    {
        var target = $"{requirement.ObjectApiName}.{requirement.FieldApiName}";
        var helperMethodName = BuildHelperMethodName(requirement.FieldApiName);
        var defaultValue = ExtractDefaultAssignmentValue(requirement);
        return new AlternativeImplementationDecision
        {
            HasAlternative = true,
            AlternativeType = "apex_trigger",
            Confidence = 0.82m,
            Label = $"Apex before-insert default for {target}",
            Description = $"Implement a before-insert Apex default for {target} when the field is blank, using the existing trigger handler/service pattern.",
            Reason = "The unsupported Flow requirement is a background record-create defaulting rule, which can be fulfilled with guarded before-insert Apex.",
            ScopeDifference = "Apex runs for all matching record creations, not only records created through the original Flow, unless the implementation adds equivalent guards.",
            Risk = "If other integrations or automation intentionally create records with this field blank, Apex would also default them unless carefully guarded.",
            SuggestedTriggerEvent = "beforeInsert",
            SuggestedHelperMethodName = helperMethodName,
            ImplementationKind = "trigger_handler",
            ImplementationStrategy = "Add a focused helper method and call it from beforeInsert.",
            EventInvocation = string.IsNullOrWhiteSpace(defaultValue)
                ? string.Empty
                : $"{helperMethodName}((List<{requirement.ObjectApiName}>) newList);",
            HelperMethodCode = string.IsNullOrWhiteSpace(defaultValue)
                ? string.Empty
                : BuildDefaultHelperMethodCode(requirement.ObjectApiName, requirement.FieldApiName, helperMethodName, defaultValue),
            TestMethodName = string.IsNullOrWhiteSpace(defaultValue)
                ? string.Empty
                : BuildDefaultTestMethodName(requirement.FieldApiName),
            TestMethodCode = string.IsNullOrWhiteSpace(defaultValue)
                ? string.Empty
                : BuildDefaultTestMethodCode(requirement.ObjectApiName, requirement.FieldApiName, BuildDefaultTestMethodName(requirement.FieldApiName), defaultValue),
            RequiresSecondAiPass = string.IsNullOrWhiteSpace(defaultValue)
        };
    }

    private async Task<AlternativeImplementationDecision?> AskModelForAlternativeAsync(
        SalesforceConfigRequirement requirement,
        string unsupportedReason)
    {
        var systemPrompt = """
You are a senior Salesforce architect reviewing unsupported Salesforce configuration requirements.
Your task is to decide whether an unsupported Flow requirement can be safely offered as an Apex alternative.

Rules:
1. Return ONLY one JSON object. No markdown.
2. Do not say an alternative exists just because Apex is powerful.
3. Only approve an Apex alternative when the business outcome can be fully implemented with Apex trigger/service logic.
4. If the requirement depends on screen-flow UI, user-facing flow fields, flow navigation, approvals, manual flow choices, or declarative-only flow behaviour, return hasAlternative=false.
5. If Apex would broaden the scope, clearly explain the scope difference and risk.
6. The alternative is only a proposal; it must not be counted as the original requested implementation.
7. When approving an Apex alternative, include likely implementation hints so the code editor can load fewer files.
8. Only include helperMethodCode and testMethodCode when you can provide valid Apex fragments. Do not use ellipses or placeholders. If unsure, leave code fragments blank and set requiresSecondAiPass=true.

JSON shape:
{
  "hasAlternative": true,
  "alternativeType": "apex_trigger",
  "confidence": 0.75,
  "label": "Apex alternative for Account.Client_Invoice_Consolidation__c",
  "description": "Implement a before-insert default when the field is blank.",
  "reason": "Flow management is unavailable, but before-insert Apex can apply the same defaulting rule.",
  "scopeDifference": "Apex applies outside the original flow unless guarded carefully.",
  "risk": "May affect Account records created by integrations or other automation.",
  "suggestedFiles": [
    "force-app/main/default/classes/AccountTriggerHandler.cls",
    "force-app/main/default/classes/AccountTriggerHandlerTest.cls"
  ],
  "suggestedTriggerEvent": "beforeInsert",
  "suggestedHelperMethodName": "defaultClientInvoiceConsolidation",
  "implementationStrategy": "Add a focused helper method and call it from beforeInsert.",
  "implementationKind": "trigger_handler",
  "eventInvocation": "defaultClientInvoiceConsolidation((List<Account>) newList);",
  "helperMethodCode": "private void defaultClientInvoiceConsolidation(List<Account> accounts) { ... }",
  "testMethodName": "testDefaultClientInvoiceConsolidation",
  "testMethodCode": "@IsTest static void testDefaultClientInvoiceConsolidation() { ... }",
  "requiresSecondAiPass": false
}
""";

        var userPrompt = $"""
Unsupported reason:
{unsupportedReason}

Requirement JSON:
{JsonSerializer.Serialize(requirement)}
""";

        var response = await _deepSeekClient.SendChatAsync(DeepSeekModels.Config, systemPrompt, userPrompt, 0.0, 1200);
        if (!TryExtractJsonObject(response, out var json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AlternativeImplementationDecision>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    private static AlternativeImplementation BuildApexAlternative(
        SalesforceConfigRequirement requirement,
        AlternativeImplementationDecision decision)
    {
        var target = string.IsNullOrWhiteSpace(requirement.FieldApiName)
            ? requirement.ObjectApiName
            : $"{requirement.ObjectApiName}.{requirement.FieldApiName}";

        var description = string.Join(" ", new[]
        {
            FirstNonBlank(decision.Description, $"Alternative Apex implementation for unsupported Flow work on {target}."),
            $"Reason: {decision.Reason}",
            $"Scope difference: {decision.ScopeDifference}",
            $"Risk: {decision.Risk}",
            "Call out in the proposed diff that this changes the implementation approach from Flow to Apex and may apply outside the original Flow unless guarded carefully.",
            $"Original Flow requirement: {requirement.Description}"
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        var alternative = new SalesforceConfigRequirement
        {
            Id = $"{requirement.Id}-APEX-ALT",
            Type = "implementation_code",
            Service = nameof(CodeEditService),
            Operation = "update",
            ObjectApiName = requirement.ObjectApiName,
            FieldApiName = requirement.FieldApiName,
            Label = FirstNonBlank(decision.Label, $"Apex alternative for {target}"),
            Description = description
        };

        ApplyImplementationHints(alternative, decision);

        return new AlternativeImplementation(
            alternative,
            FirstNonBlank(decision.Reason, "FlowManagementService is not implemented yet. The model identified an Apex alternative, but it changes the implementation approach and should be reviewed before approval."));
    }

    private static void ApplyImplementationHints(
        SalesforceConfigRequirement alternative,
        AlternativeImplementationDecision decision)
    {
        var objectName = StripCustomObjectSuffix(alternative.ObjectApiName);
        if (string.IsNullOrWhiteSpace(objectName))
        {
            return;
        }

        alternative.SuggestedTriggerEvent = FirstNonBlank(decision.SuggestedTriggerEvent, "beforeInsert");
        alternative.SuggestedHelperMethodName = FirstNonBlank(
            decision.SuggestedHelperMethodName,
            BuildHelperMethodName(alternative.FieldApiName));
        alternative.ImplementationStrategy = FirstNonBlank(
            decision.ImplementationStrategy,
            $"Use the existing {objectName}TriggerHandler pattern: add a helper method and invoke it from {alternative.SuggestedTriggerEvent}.");
        alternative.ImplementationKind = FirstNonBlank(decision.ImplementationKind, "trigger_handler");
        alternative.EventInvocation = decision.EventInvocation;
        alternative.HelperMethodCode = decision.HelperMethodCode;
        alternative.TestMethodName = decision.TestMethodName;
        alternative.TestMethodCode = decision.TestMethodCode;
        alternative.RequiresSecondAiPass = decision.RequiresSecondAiPass;

        AddIfMissing(alternative.SuggestedFiles, $"force-app/main/default/classes/{objectName}TriggerHandler.cls");
        AddIfMissing(alternative.SuggestedFiles, $"force-app/main/default/classes/{objectName}TriggerHandlerTest.cls");

        foreach (var suggestedFile in decision.SuggestedFiles)
        {
            AddIfMissing(alternative.SuggestedFiles, suggestedFile);
        }
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
        if (parts.Length == 0)
        {
            return "applyApexAlternative";
        }

        return "default" + string.Concat(parts.Select(part =>
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
        return $$"""
@IsTest
static void {{testMethodName}}() {
    {{objectApiName}} {{variableName}} = new {{objectApiName}}(Name = 'Test {{StripCustomObjectSuffix(objectApiName)}}');
    insert {{variableName}};

    {{variableName}} = [SELECT {{fieldApiName}} FROM {{objectApiName}} WHERE Id = :{{variableName}}.Id];
    System.assertEquals('{{EscapeApexString(defaultValue)}}', {{variableName}}.{{fieldApiName}}, '{{fieldApiName}} should default on insert.');
}
""";
    }

    private static string BuildRecordVariableName(string objectApiName)
    {
        var baseName = StripCustomObjectSuffix(objectApiName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return "record";
        }

        return char.ToLowerInvariant(baseName[0]) + baseName[1..] + "Record";
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

    private static void AddIfMissing(List<string> values, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!values.Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
        {
            values.Add(value);
        }
    }

    private static bool IsApexAlternative(string value)
    {
        return ContainsAny(value, "apex", "trigger", "service");
    }

    private static bool TryExtractJsonObject(string value, out string json)
    {
        json = string.Empty;
        var firstBrace = value.IndexOf('{');
        var lastBrace = value.LastIndexOf('}');
        if (firstBrace < 0 || lastBrace <= firstBrace)
        {
            return false;
        }

        json = value[firstBrace..(lastBrace + 1)];
        return true;
    }

    private static string FirstNonBlank(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static bool ContainsAny(string source, params string[] values)
    {
        return values.Any(value => source.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class AlternativeImplementationDecision
    {
        public bool HasAlternative { get; set; }
        public string AlternativeType { get; set; } = string.Empty;
        public decimal Confidence { get; set; }
        public string Label { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string ScopeDifference { get; set; } = string.Empty;
        public string Risk { get; set; } = string.Empty;
        public List<string> SuggestedFiles { get; set; } = new();
        public string SuggestedTriggerEvent { get; set; } = string.Empty;
        public string SuggestedHelperMethodName { get; set; } = string.Empty;
        public string ImplementationStrategy { get; set; } = string.Empty;
        public string ImplementationKind { get; set; } = string.Empty;
        public string EventInvocation { get; set; } = string.Empty;
        public string HelperMethodCode { get; set; } = string.Empty;
        public string TestMethodName { get; set; } = string.Empty;
        public string TestMethodCode { get; set; } = string.Empty;
        public bool RequiresSecondAiPass { get; set; }
    }
}

public sealed record AlternativeImplementation(SalesforceConfigRequirement Requirement, string Reason);
