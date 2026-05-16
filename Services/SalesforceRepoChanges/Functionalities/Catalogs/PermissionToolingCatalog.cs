namespace eZBERP_AI_IDE.Services;

public static class PermissionToolingCatalog
{
    public const string UnsupportedRequirementMessage = "System is not configured for this requirement.";

    public static readonly HashSet<string> ProfileRequirementTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "profile_metadata",
        "profile_fls_update"
    };

    public static readonly HashSet<string> PermissionSetRequirementTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "permission_set",
        "permission_set_fls_update"
    };

    public static readonly HashSet<string> CustomPermissionRequirementTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "custom_permission"
    };

    public static readonly HashSet<string> SupportedRequirementTypes = new(ProfileRequirementTypes
        .Concat(PermissionSetRequirementTypes)
        .Concat(CustomPermissionRequirementTypes), StringComparer.OrdinalIgnoreCase);

    public static readonly HashSet<string> SupportedPermissionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "fls",
        "tab",
        "apex_class",
        "apex_page",
        "object",
        "custom_permission",
        "record_type",
        "application",
        "user_permission"
    };

    public static bool IsSupportedRequirementType(string? type)
        => !string.IsNullOrWhiteSpace(type) && SupportedRequirementTypes.Contains(type);

    public static bool IsProfileRequirement(string? type)
        => !string.IsNullOrWhiteSpace(type) && ProfileRequirementTypes.Contains(type);

    public static bool IsPermissionSetRequirement(string? type)
        => !string.IsNullOrWhiteSpace(type) && PermissionSetRequirementTypes.Contains(type);

    public static bool IsCustomPermissionRequirement(string? type)
        => !string.IsNullOrWhiteSpace(type) && CustomPermissionRequirementTypes.Contains(type);

    public static bool IsSupportedPermissionType(string? permissionType)
        => string.IsNullOrWhiteSpace(permissionType) || SupportedPermissionTypes.Contains(permissionType);
}
