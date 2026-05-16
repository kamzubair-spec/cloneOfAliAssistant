namespace eZBERP_AI_IDE.Models;

public sealed class SalesforceConfigPlan
{
    public string Summary { get; set; } = string.Empty;
    public List<SalesforceConfigRequirement> Requirements { get; set; } = new();
    public List<string> Questions { get; set; } = new();
}

public sealed class SalesforceConfigRequirement
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string ObjectApiName { get; set; } = string.Empty;
    public string FieldApiName { get; set; } = string.Empty;
    public string FieldType { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int? Length { get; set; }
    public bool? Required { get; set; }
    public string DefaultValue { get; set; } = string.Empty;
    public string InlineHelpText { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string FieldDescription { get; set; } = string.Empty;
    public string Formula { get; set; } = string.Empty;
    public string FormulaReturnType { get; set; } = string.Empty;
    public string ExistingFieldApiName { get; set; } = string.Empty;
    public string TargetMetadataName { get; set; } = string.Empty;
    public string TargetSectionLabel { get; set; } = string.Empty;
    public string ReplaceFieldApiName { get; set; } = string.Empty;
    public string VisibilityConditionSummary { get; set; } = string.Empty;
    public string PreferredTargetType { get; set; } = string.Empty;
    public string TargetRegionOrComponent { get; set; } = string.Empty;
    public string TargetLayoutOrPageLabel { get; set; } = string.Empty;
    public string ValidationRuleName { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string ErrorLocation { get; set; } = string.Empty;
    public List<string> PicklistValues { get; set; } = new();
    public List<PicklistValueRequirement> PicklistEntries { get; set; } = new();
    public List<PicklistValueRenameRequirement> PicklistRenames { get; set; } = new();
    public bool KeepPicklistValuesInOrder { get; set; }
    public bool AddGlobalValueSetValuesToAllRecordTypes { get; set; }
    public List<string> PermissionSetNames { get; set; } = new();
    public string CustomMetadataTypeApiName { get; set; } = string.Empty;
    public string RecordDeveloperName { get; set; } = string.Empty;
    public Dictionary<string, string> CustomMetadataValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ProfileAccessRequirement? ProfileAccess { get; set; }
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

public sealed record FileEditPlan(string Path, List<CodeEdit> Edits);

public sealed record CodeEdit(string Search, string Replace, string Reason = "");

public sealed class PicklistValueRequirement
{
    public string ApiValue { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public bool Default { get; set; }
    public List<string> ControllingValues { get; set; } = new();
}

public sealed class PicklistValueRenameRequirement
{
    public string CurrentApiValue { get; set; } = string.Empty;
    public string CurrentLabel { get; set; } = string.Empty;
    public string NewLabel { get; set; } = string.Empty;
}

public sealed class ProfileAccessRequirement
{
    public List<string> EditableProfiles { get; set; } = new();
    public List<string> ReadOnlyProfiles { get; set; } = new();
    public bool ApplyReadOnlyToRemainingProfiles { get; set; }
}

