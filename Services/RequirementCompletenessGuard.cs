using eZBERP_AI_IDE.Models;

namespace eZBERP_AI_IDE.Services;

public static class RequirementCompletenessGuard
{
    public static SalesforceConfigPlan AddConservativeUnsupportedItems(string story, SalesforceConfigPlan plan)
    {
        var guarded = new SalesforceConfigPlan
        {
            Summary = plan.Summary,
            Questions = new List<string>(plan.Questions),
            Requirements = new List<SalesforceConfigRequirement>(plan.Requirements)
        };

        if (string.IsNullOrWhiteSpace(story))
        {
            return guarded;
        }

        var normalizedStory = NormalizeText(story);
        AddExternalOrganisationLayoutIfNeeded(normalizedStory, guarded);
        AddContractPlacementLayoutIfNeeded(normalizedStory, guarded);
        AddDynamicVisibilityIfNeeded(normalizedStory, guarded);
        AddQuickActionIfNeeded(normalizedStory, guarded);
        AddFlowIfNeeded(normalizedStory, guarded);
        AddRecordTypeIfNeeded(normalizedStory, guarded);
        AddNamedUnsupportedConfigIfNeeded(normalizedStory, guarded, "custom metadata", "custom_metadata", "Custom metadata changes were mentioned but no deterministic custom metadata service is implemented yet.");
        AddNamedUnsupportedConfigIfNeeded(normalizedStory, guarded, "custom permission", "custom_permission", "Custom permission changes were mentioned but no deterministic custom permission service is implemented yet.");
        AddNamedUnsupportedConfigIfNeeded(normalizedStory, guarded, "custom label", "custom_label", "Custom label changes were mentioned but no deterministic custom label service is implemented yet.");

        return guarded;
    }

    private static void AddExternalOrganisationLayoutIfNeeded(string story, SalesforceConfigPlan plan)
    {
        if (!ContainsAny(story, "page layout: organisation", "page layout: organization", "organisation page layout", "organization page layout"))
        {
            return;
        }

        if (!ContainsAny(story, "another ticket", "in development", "being updated in ticket", "no action"))
        {
            return;
        }

        if (plan.Requirements.Any(IsExternalOrganisationLayoutReference))
        {
            return;
        }

        AddIfMissing(plan, "external_dependency", string.Empty, "review", "Account", string.Empty,
            "Organisation page layout is referenced as handled outside this change.",
            "The story says the Organisation page layout is handled by another ticket, so this app should not silently count it as supported work.");
    }

    private static void AddContractPlacementLayoutIfNeeded(string story, SalesforceConfigPlan plan)
    {
        if (!ContainsAny(story, "page layout: contract placement", "contract placement page layout", "placement layout"))
        {
            return;
        }

        if (ContainsAny(story, "organisation details", "organization details")
            && ContainsAny(story, "invoice consolidation option", "client invoice consolidation", "quick action", "create a new action", "new action"))
        {
            // The generic quick-action resolver can inspect flexipage related-record components for this.
            // Do not add a blank Placement__c layout fallback that produces a misleading unsupported item.
            return;
        }

        AddIfMissing(plan, "layout", "LayoutManagementService", "update", "Placement__c", string.Empty,
            "Contract Placement page layout update.",
            "The story asks for a layout field replacement/placement on the Contract Placement page layout.");
    }

    private static void AddDynamicVisibilityIfNeeded(string story, SalesforceConfigPlan plan)
    {
        if (!ContainsAny(story, "only be visible", "visible on the placement layout", "display depending", "visibility rule", "visibility criteria", "visibility criterias", "component visibility", "change visibility", "update visibility"))
        {
            return;
        }

        if (LooksLikeQuickActionDisplayVariant(story))
        {
            // A story that asks for a new action/quick-action variant should be represented
            // as the unsupported action creation itself, not as a second vague flexipage item.
            return;
        }

        if (plan.Requirements.Any(requirement => requirement.Type.Equals("flexipage", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var objectApiName = InferObjectApiNameFromStory(story);
        var targetPage = ExtractBracketedPageName(story);
        var section = ExtractBillingSectionSummary(story);

        var requirement = new SalesforceConfigRequirement
        {
            Id = $"AUTO-{plan.Requirements.Count + 1:000}",
            Type = "flexipage",
            Service = "FlexipageManagementService",
            Operation = "update",
            ObjectApiName = objectApiName,
            TargetLayoutOrPageLabel = targetPage,
            TargetSectionLabel = section,
            Label = "Dynamic visibility rule for config UI.",
            VisibilityConditionSummary = "Update field visibility criteria described by the story.",
            Description = "The story asks for page visibility criteria changes. Exact field references may need image-derived context if not present in text."
        };

        plan.Requirements.Add(requirement);
    }

    private static bool IsExternalOrganisationLayoutReference(SalesforceConfigRequirement requirement)
    {
        if (!requirement.Type.Equals("external_dependency", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var text = NormalizeText($"{requirement.ObjectApiName} {requirement.Label} {requirement.Description} {requirement.TargetLayoutOrPageLabel}");
        return ContainsAny(text, "organisation page layout", "organization page layout", "page layout organisation", "page layout organization")
               || (ContainsAny(text, "organisation", "organization", "account")
                   && ContainsAny(text, "another ticket", "handled outside", "outside this change", "being handled", "in development"));
    }

    private static bool LooksLikeQuickActionDisplayVariant(string story)
    {
        return ContainsAny(story, "quick action", "create a new action", "new action")
               && ContainsAny(story, "display depending", "visible only if", "only be visible", "visibility", "depending on")
               && ContainsAny(story, "organisation details", "organization details", "section displays organisation fields", "section displays organization fields");
    }

    private static void AddQuickActionIfNeeded(string story, SalesforceConfigPlan plan)
    {
        if (!ContainsAny(story, "quick action", "create a new action", "new action"))
        {
            return;
        }


        if (ContainsAny(story, "create a new action", "new action"))
        {
            AddIfMissing(plan, "quick_action", "QuickActionManagementService", "create", "Account", string.Empty,
                "New Organisation Details quick action variant.",
                "The story says a new action is needed for conditional display. Creating new quick actions is outside the current deterministic quick-action scope.");
            return;
        }

        AddIfMissing(plan, "quick_action", "QuickActionManagementService", "update", string.Empty, string.Empty,
            "Quick action configuration update.",
            "The story asks for an existing quick action configuration update.");
    }
    private static void AddFlowIfNeeded(string story, SalesforceConfigPlan plan)
    {
        if (!ContainsAny(story, "flow", "created via a flow", "flow logic"))
        {
            return;
        }

        var existingFlowLike = plan.Requirements.FirstOrDefault(requirement =>
            requirement.Type.Equals("flow", StringComparison.OrdinalIgnoreCase) ||
            (requirement.Type.Equals("implementation_code", StringComparison.OrdinalIgnoreCase)
             && ContainsAny($"{requirement.Label} {requirement.Description}", "flow", "created via a flow", "flow logic")));

        if (existingFlowLike is not null)
        {
            existingFlowLike.Type = "flow";
            existingFlowLike.Service = "FlowManagementService";
            existingFlowLike.Operation = string.IsNullOrWhiteSpace(existingFlowLike.Operation) ? "update" : existingFlowLike.Operation;
            if (string.IsNullOrWhiteSpace(existingFlowLike.ObjectApiName))
            {
                existingFlowLike.ObjectApiName = InferObjectApiNameFromStory(story);
            }

            if (string.IsNullOrWhiteSpace(existingFlowLike.FieldApiName))
            {
                existingFlowLike.FieldApiName = InferFieldApiNameFromStory(story);
            }

            return;
        }

        AddIfMissing(plan, "flow", "FlowManagementService", "update", InferObjectApiNameFromStory(story), InferFieldApiNameFromStory(story),
            "Flow configuration update.",
            "The story asks for flow logic/configuration. FlowManagementService is not implemented yet.");
    }
    private static void AddRecordTypeIfNeeded(string story, SalesforceConfigPlan plan)
    {
        if (!story.Contains("record type", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (ContainsAny(story, "no longer needs to look at the supplier record type", "no longer needs to look at supplier record type", "no longer needs to look at the record type", "no longer needs to look at record type"))
        {
            return;
        }

        if (!ContainsAny(story, "create record type", "update record type", "record type value", "record type picklist", "record type assignment", "record type visibility", "record type support"))
        {
            return;
        }

        AddIfMissing(plan, "record_type", "ObjectManagementService", "update", string.Empty, string.Empty,
            "Record type metadata update.",
            "Record type metadata changes were mentioned but deterministic record type handling is not implemented yet.");
    }

    private static void AddNamedUnsupportedConfigIfNeeded(string story, SalesforceConfigPlan plan, string keyword, string type, string description)
    {
        if (!story.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AddIfMissing(plan, type, string.Empty, "review", string.Empty, string.Empty, keyword, description);
    }

    private static void AddIfMissing(
        SalesforceConfigPlan plan,
        string type,
        string service,
        string operation,
        string objectApiName,
        string fieldApiName,
        string label,
        string description)
    {
        if (plan.Requirements.Any(requirement =>
                requirement.Type.Equals(type, StringComparison.OrdinalIgnoreCase)
                && SimilarEnough(requirement, label, objectApiName, description)))
        {
            return;
        }

        plan.Requirements.Add(new SalesforceConfigRequirement
        {
            Id = $"AUTO-{plan.Requirements.Count + 1:000}",
            Type = type,
            Service = service,
            Operation = operation,
            ObjectApiName = objectApiName,
            FieldApiName = fieldApiName,
            Label = label,
            Description = description
        });
    }

    private static bool SimilarEnough(SalesforceConfigRequirement requirement, string label, string objectApiName, string description)
    {
        var haystack = NormalizeText($"{requirement.Label} {requirement.Description} {requirement.ObjectApiName}");
        return (!string.IsNullOrWhiteSpace(objectApiName) && requirement.ObjectApiName.Equals(objectApiName, StringComparison.OrdinalIgnoreCase))
               || (!string.IsNullOrWhiteSpace(label) && haystack.Contains(NormalizeText(label), StringComparison.OrdinalIgnoreCase))
               || (!string.IsNullOrWhiteSpace(description) && haystack.Contains(NormalizeText(description).Split(' ')[0], StringComparison.OrdinalIgnoreCase));
    }

    private static string StripVerificationSections(string story)
    {
        var markers = new[]
        {
            " acceptance criteria ",
            " business problem statement ",
            " assumptions ",
            " reported by ",
            " subtasks ",
            " linked work items "
        };

        var firstMarker = markers
            .Select(marker => story.IndexOf(marker, StringComparison.OrdinalIgnoreCase))
            .Where(index => index >= 0)
            .DefaultIfEmpty(-1)
            .Min();

        return firstMarker >= 0 ? story[..firstMarker] : story;
    }
    private static bool ContainsAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeText(string value)
    {
        return string.Join(" ", (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string InferObjectApiNameFromStory(string story)
    {
        if (ContainsAny(story, "organisation", "organization", "account"))
        {
            return "Account";
        }

        if (ContainsAny(story, "placement"))
        {
            return "Placement__c";
        }

        if (ContainsAny(story, "supplier"))
        {
            return "Supplier__c";
        }

        return string.Empty;
    }

    private static string ExtractBracketedPageName(string story)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            story,
            @"page layout\s*\[(?<name>[^\]]+)\]|page\s*\[(?<name>[^\]]+)\]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (match.Success)
        {
            return match.Groups["name"].Value.Trim();
        }

        match = System.Text.RegularExpressions.Regex.Match(
            story,
            @"\b(?<name>[A-Za-z]+(?:\s+[A-Za-z]+){0,4}\s+(?:Revolution\s+)?Page)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return match.Success ? match.Groups["name"].Value.Trim() : string.Empty;
    }

    private static string ExtractBillingSectionSummary(string story)
    {
        var sections = new List<string>();
        if (ContainsAny(story, "contract billing"))
        {
            sections.Add("Contract Billing");
        }

        if (ContainsAny(story, "perm billing"))
        {
            sections.Add("Perm Billing");
        }

        if (sections.Count == 0)
        {
            return string.Empty;
        }

        var prefix = ContainsAny(story, "admin tab", "billing sub tab")
            ? "Admin Tab > Billing sub tab > "
            : string.Empty;

        return prefix + string.Join(" / ", sections);
    }

    private static string InferFieldApiNameFromStory(string story)
    {
        var explicitApiName = System.Text.RegularExpressions.Regex.Match(story, @"\b[A-Za-z][A-Za-z0-9_]*__c\b");
        if (explicitApiName.Success)
        {
            return explicitApiName.Value;
        }

        var fieldMatch = System.Text.RegularExpressions.Regex.Match(
            story,
            @"field\s+['""“”]?(?<name>[A-Za-z][A-Za-z0-9\s/&-]{2,80}?)(?:['""“”]?(\s|$|\.|,|;|:))",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!fieldMatch.Success)
        {
            return string.Empty;
        }

        var name = System.Text.RegularExpressions.Regex.Replace(
            fieldMatch.Groups["name"].Value.Trim(),
            @"\s+(to|with|when|on|in|is|should|must|needs)\b.*$",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

        var words = System.Text.RegularExpressions.Regex.Matches(name, @"[A-Za-z0-9]+")
            .Select(match => match.Value)
            .Where(word => !string.IsNullOrWhiteSpace(word))
            .Select(word => char.ToUpperInvariant(word[0]) + (word.Length > 1 ? word[1..] : string.Empty))
            .ToList();

        return words.Count == 0 ? string.Empty : $"{string.Join("_", words)}__c";
    }
}









