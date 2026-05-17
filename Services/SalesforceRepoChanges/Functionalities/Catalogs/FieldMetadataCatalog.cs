namespace eZBERP_AI_IDE.Services;

public static class FieldMetadataCatalog
{
    public static readonly HashSet<string> SupportedRequirementTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "custom_field",
        "field_metadata"
    };

    public static readonly HashSet<string> SupportedCreateFieldTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text",
        "textarea",
        "longtextarea",
        "number",
        "currency",
        "percent",
        "checkbox",
        "date",
        "datetime",
        "picklist",
        "multiselectpicklist",
        "lookup",
        "masterdetail"
    };

    public static bool IsSupportedRequirementType(string? type)
        => !string.IsNullOrWhiteSpace(type) && SupportedRequirementTypes.Contains(type);

    public static bool IsSupportedCreateFieldType(string? type)
        => !string.IsNullOrWhiteSpace(type) && SupportedCreateFieldTypes.Contains(NormalizeFieldType(type));

    public static string NormalizeFieldType(string? type)
    {
        var value = (type ?? string.Empty).Trim().Replace(" ", string.Empty).Replace("-", string.Empty);
        return value.ToLowerInvariant() switch
        {
            "multiselectpicklist" or "multiselect" => "multiselectpicklist",
            "longtext" or "longtextarea" => "longtextarea",
            "masterdetail" or "master-detail" => "masterdetail",
            _ => value.ToLowerInvariant()
        };
    }

    public static bool IsCompatibleFieldUpdate(string existingType, string requestedType)
    {
        var normalizedExisting = NormalizeFieldType(existingType);
        var normalizedRequested = NormalizeFieldType(requestedType);
        return string.IsNullOrWhiteSpace(normalizedRequested)
               || normalizedExisting.Equals(normalizedRequested, StringComparison.OrdinalIgnoreCase);
    }

    public static bool RequiresValueSetSource(string? fieldType)
        => NormalizeFieldType(fieldType) is "picklist" or "multiselectpicklist";

    public static bool RequiresRelationshipTarget(string? fieldType)
        => NormalizeFieldType(fieldType) is "lookup" or "masterdetail";
}
