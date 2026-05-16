using System.Text;
using System.Text.RegularExpressions;
using eZBERP_AI_IDE.Models;
using File = System.IO.File;

namespace eZBERP_AI_IDE.Services;

public sealed class ObjectManagementService : IConfigWorkItemHandler
{
    public string ServiceName => nameof(ObjectManagementService);

    public bool CanHandle(SalesforceConfigRequirement requirement)
    {
        return CanHandleFieldRequirement(requirement)
               || CanHandlePicklistRequirement(requirement)
               || CanHandleValidationRuleRequirement(requirement);
    }

    public async Task<FileChangeSet?> BuildChangeSetAsync(string repoPath, SalesforceConfigRequirement requirement)
    {
        if (IsValidationRuleRequirement(requirement))
        {
            return await BuildValidationRuleChangeSetAsync(repoPath, requirement);
        }

        var objectApiName = NormalizeObjectApiName(requirement.ObjectApiName);
        var fieldApiName = NormalizeFieldApiName(requirement.FieldApiName);
        if (string.IsNullOrWhiteSpace(objectApiName) || string.IsNullOrWhiteSpace(fieldApiName))
        {
            throw new InvalidOperationException("Field metadata changes require both objectApiName and fieldApiName.");
        }

        var relativePath = Path.Combine("force-app", "main", "default", "objects", objectApiName, "fields", $"{fieldApiName}.field-meta.xml");
        var fullPath = Path.Combine(repoPath, relativePath);
        if (!File.Exists(fullPath) && TryResolveExistingFieldPath(repoPath, objectApiName, fieldApiName, out var resolvedRelativePath, out var resolvedFieldApiName))
        {
            relativePath = resolvedRelativePath;
            fullPath = Path.Combine(repoPath, relativePath);
            fieldApiName = resolvedFieldApiName;
        }

        var exists = File.Exists(fullPath);

        if (requirement.Type.Equals("field_create", StringComparison.OrdinalIgnoreCase) && exists)
        {
            throw new InvalidOperationException($"The requested field already exists: {objectApiName}.{fieldApiName}. Ask for a field update or FLS update instead.");
        }

        if (IsPicklistRequirement(requirement) && !exists)
        {
            throw new FileNotFoundException($"Cannot add picklist values because field metadata was not found: {relativePath}", fullPath);
        }

        var existingContent = exists ? await File.ReadAllTextAsync(fullPath) : string.Empty;
        var proposedContent = IsPicklistRequirement(requirement)
            ? AddPicklistValues(existingContent, requirement)
            : exists
                ? UpdateExistingField(existingContent, requirement)
                : BuildNewFieldXml(fieldApiName, requirement);

        return new FileChangeSet(
            $"Object metadata change for {objectApiName}.{fieldApiName}",
            new[] { new FileChangeProposal(relativePath, existingContent, proposedContent, exists) });
    }

    private static async Task<FileChangeSet> BuildValidationRuleChangeSetAsync(string repoPath, SalesforceConfigRequirement requirement)
    {
        var objectApiName = NormalizeObjectApiName(requirement.ObjectApiName);
        if (string.IsNullOrWhiteSpace(objectApiName))
        {
            throw new InvalidOperationException("Validation rule changes require objectApiName.");
        }

        var ruleName = NormalizeValidationRuleName(requirement.ValidationRuleName, requirement.Label);
        if (string.IsNullOrWhiteSpace(ruleName))
        {
            throw new InvalidOperationException("Validation rule changes require validationRuleName.");
        }

        var relativePath = Path.Combine("force-app", "main", "default", "objects", objectApiName, "validationRules", $"{ruleName}.validationRule-meta.xml");
        var fullPath = Path.Combine(repoPath, relativePath);
        var existingContent = File.Exists(fullPath) ? await File.ReadAllTextAsync(fullPath) : string.Empty;
        var proposedContent = BuildValidationRuleXml(ruleName, requirement);

        return new FileChangeSet(
            $"Validation rule metadata change for {objectApiName}.{ruleName}",
            new[] { new FileChangeProposal(relativePath, existingContent, proposedContent, File.Exists(fullPath)) });
    }

    private static bool CanHandleFieldRequirement(SalesforceConfigRequirement requirement)
    {
        return IsFieldRequirement(requirement)
               && !string.IsNullOrWhiteSpace(requirement.ObjectApiName)
               && !string.IsNullOrWhiteSpace(requirement.FieldApiName);
    }

    private static bool CanHandlePicklistRequirement(SalesforceConfigRequirement requirement)
    {
        return IsPicklistRequirement(requirement)
               && !string.IsNullOrWhiteSpace(requirement.ObjectApiName)
               && !string.IsNullOrWhiteSpace(requirement.FieldApiName)
               && (requirement.PicklistEntries.Count > 0 || requirement.PicklistValues.Count > 0 || !string.IsNullOrWhiteSpace(requirement.DefaultValue));
    }

    private static bool CanHandleValidationRuleRequirement(SalesforceConfigRequirement requirement)
    {
        return IsValidationRuleRequirement(requirement)
               && !string.IsNullOrWhiteSpace(requirement.ObjectApiName)
               && !string.IsNullOrWhiteSpace(FirstNonBlank(requirement.ValidationRuleName, requirement.Label))
               && !string.IsNullOrWhiteSpace(requirement.Formula);
    }
    private static bool IsFieldRequirement(SalesforceConfigRequirement requirement)
    {
        return requirement.Type.Equals("field_create", StringComparison.OrdinalIgnoreCase)
               || requirement.Type.Equals("field_update", StringComparison.OrdinalIgnoreCase)
               || requirement.Type.Equals("field_upsert", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPicklistRequirement(SalesforceConfigRequirement requirement)
    {
        return requirement.Type.Equals("picklist", StringComparison.OrdinalIgnoreCase)
               || requirement.Type.Equals("picklist_value", StringComparison.OrdinalIgnoreCase)
               || requirement.Type.Equals("picklist_value_add", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidationRuleRequirement(SalesforceConfigRequirement requirement)
    {
        return requirement.Type.Equals("validation_rule", StringComparison.OrdinalIgnoreCase)
               || requirement.Type.Equals("validation_rule_create", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildNewFieldXml(string fieldApiName, SalesforceConfigRequirement requirement)
    {
        var fieldType = NormalizeFieldType(requirement.FieldType);
        var label = string.IsNullOrWhiteSpace(requirement.Label)
            ? BuildLabelFromApiName(fieldApiName)
            : requirement.Label.Trim();
        var helpText = FirstNonBlank(requirement.InlineHelpText, requirement.FieldDescription);

        var builder = new StringBuilder();
        builder.AppendLine(@"<?xml version=""1.0"" encoding=""UTF-8""?>");
        builder.AppendLine(@"<CustomField xmlns=""http://soap.sforce.com/2006/04/metadata"">");
        builder.AppendLine($"    <fullName>{fieldApiName}</fullName>");

        if (fieldType.Equals("Checkbox", StringComparison.OrdinalIgnoreCase))
        {
            builder.AppendLine($"    <defaultValue>{NormalizeCheckboxDefault(requirement.DefaultValue)}</defaultValue>");
        }

        if (!string.IsNullOrWhiteSpace(requirement.FieldDescription))
        {
            builder.AppendLine($"    <description>{EscapeXml(requirement.FieldDescription.Trim())}</description>");
        }

        builder.AppendLine("    <externalId>false</externalId>");

        if (!string.IsNullOrWhiteSpace(helpText))
        {
            builder.AppendLine($"    <inlineHelpText>{EscapeXml(helpText.Trim())}</inlineHelpText>");
        }

        builder.AppendLine($"    <label>{EscapeXml(label)}</label>");

        if (fieldType.Equals("Text", StringComparison.OrdinalIgnoreCase))
        {
            builder.AppendLine($"    <length>{requirement.Length.GetValueOrDefault(255)}</length>");
        }

        if (fieldType.Equals("Formula", StringComparison.OrdinalIgnoreCase))
        {
            builder.AppendLine($"    <formula>{EscapeXml(requirement.Formula.Trim())}</formula>");
            builder.AppendLine("    <formulaTreatBlanksAs>BlankAsZero</formulaTreatBlanksAs>");
        }

        builder.AppendLine($"    <required>{requirement.Required.GetValueOrDefault(false).ToString().ToLowerInvariant()}</required>");
        builder.AppendLine("    <trackFeedHistory>false</trackFeedHistory>");
        builder.AppendLine("    <trackHistory>false</trackHistory>");
        builder.AppendLine("    <trackTrending>false</trackTrending>");
        builder.AppendLine($"    <type>{fieldType}</type>");
        builder.AppendLine("</CustomField>");
        return builder.ToString().TrimEnd();
    }

    private static string UpdateExistingField(string existingContent, SalesforceConfigRequirement requirement)
    {
        var proposed = existingContent;
        proposed = UpdateTagIfValueProvided(proposed, "label", requirement.Label);
        proposed = UpdateTagIfValueProvided(proposed, "inlineHelpText", requirement.InlineHelpText);
        proposed = UpdateTagIfValueProvided(proposed, "description", requirement.FieldDescription);
        proposed = UpdateTagIfValueProvided(proposed, "defaultValue", requirement.DefaultValue);

        if (!string.IsNullOrWhiteSpace(requirement.Formula))
        {
            proposed = UpsertTag(proposed, "formula", EscapeXml(requirement.Formula.Trim()));
        }

        if (requirement.Length.HasValue)
        {
            proposed = UpsertTag(proposed, "length", requirement.Length.Value.ToString());
        }

        if (requirement.Required.HasValue)
        {
            proposed = UpsertTag(proposed, "required", requirement.Required.Value.ToString().ToLowerInvariant());
        }

        return proposed;
    }

    private static string AddPicklistValues(string existingContent, SalesforceConfigRequirement requirement)
    {
        var entries = BuildPicklistEntries(requirement);
        if (entries.Count == 0)
        {
            throw new InvalidOperationException("Picklist value changes require at least one picklist entry.");
        }

        if (!existingContent.Contains("<valueSetDefinition>", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only local valueSetDefinition picklist fields are supported by this service.");
        }

        var proposed = existingContent;
        var insertionIndex = proposed.IndexOf("        </valueSetDefinition>", StringComparison.OrdinalIgnoreCase);
        if (insertionIndex < 0)
        {
            insertionIndex = proposed.IndexOf("</valueSetDefinition>", StringComparison.OrdinalIgnoreCase);
        }

        if (insertionIndex < 0)
        {
            throw new InvalidOperationException("Could not find valueSetDefinition closing tag.");
        }

        proposed = requirement.KeepPicklistValuesInOrder
            ? AddPicklistValuesInOrder(proposed, entries)
            : AppendPicklistValues(proposed, entries, insertionIndex);

        return AddDependentPicklistValueSettings(proposed, requirement, entries);
    }

    private static string AppendPicklistValues(string content, IReadOnlyList<PicklistValueRequirement> entries, int insertionIndex)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            if (HasPicklistValue(content, entry.ApiValue))
            {
                continue;
            }

            builder.AppendLine(BuildPicklistValueBlock(entry));
        }

        return builder.Length == 0 ? content : content.Insert(insertionIndex, builder.ToString());
    }

    private static string AddPicklistValuesInOrder(string content, IReadOnlyList<PicklistValueRequirement> entries)
    {
        var matches = Regex.Matches(content, @"\s*<value><fullName>.*?</value>\s*", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (matches.Count == 0)
        {
            return content;
        }

        var proposed = content;
        var existingValues = matches
            .Cast<Match>()
            .Select(match => new PicklistValueMatch(
                ExtractTagValue(match.Value, "fullName") ?? string.Empty,
                match.Index,
                match.Length,
                DetectIndent(content, match.Index)))
            .Where(value => !string.IsNullOrWhiteSpace(value.ApiValue))
            .ToList();

        foreach (var entry in entries)
        {
            if (existingValues.Any(value => value.ApiValue.Equals(entry.ApiValue, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var insertionIndex = ResolveOrderedPicklistInsertionIndex(proposed, existingValues, entry.ApiValue);
            var insertionBlock = BuildPicklistValueBlock(entry).TrimEnd('\r', '\n') + GetLineEnding(proposed);
            proposed = proposed.Insert(insertionIndex, insertionBlock);

            existingValues = existingValues
                .Select(value => value.StartIndex >= insertionIndex
                    ? value with { StartIndex = value.StartIndex + insertionBlock.Length }
                    : value)
                .Append(new PicklistValueMatch(entry.ApiValue, insertionIndex, insertionBlock.Length, DetectIndent(insertionBlock, 0)))
                .OrderBy(value => value.StartIndex)
                .ToList();
        }

        return proposed;
    }

    private static int ResolveOrderedPicklistInsertionIndex(string content, IReadOnlyList<PicklistValueMatch> existingValues, string newApiValue)
    {
        var nextValue = existingValues
            .Where(value => string.Compare(newApiValue, value.ApiValue, StringComparison.OrdinalIgnoreCase) < 0)
            .OrderBy(value => value.StartIndex)
            .FirstOrDefault();

        if (nextValue is not null)
        {
            return GetLineStart(content, nextValue.StartIndex);
        }

        var lastValue = existingValues.OrderBy(value => value.StartIndex).Last();
        return GetNextLineStart(content, lastValue.StartIndex + lastValue.Length);
    }

    private static bool HasPicklistValue(string content, string apiValue)
    {
        return Regex.IsMatch(content, $@"<value>\s*<fullName>{Regex.Escape(apiValue)}</fullName>.*?</value>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    }

    private static string BuildPicklistValueBlock(PicklistValueRequirement entry)
    {
        return $"            <value><fullName>{EscapeXml(entry.ApiValue)}</fullName><default>{entry.Default.ToString().ToLowerInvariant()}</default><label>{EscapeXml(entry.Label)}</label></value>";
    }

    private static List<PicklistValueRequirement> BuildPicklistEntries(SalesforceConfigRequirement requirement)
    {
        var entries = requirement.PicklistEntries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.ApiValue) || !string.IsNullOrWhiteSpace(entry.Label))
            .Select(entry => new PicklistValueRequirement
            {
                ApiValue = string.IsNullOrWhiteSpace(entry.ApiValue) ? BuildApiValueFromLabel(entry.Label) : entry.ApiValue.Trim(),
                Label = string.IsNullOrWhiteSpace(entry.Label) ? BuildLabelFromApiValue(entry.ApiValue) : entry.Label.Trim(),
                Default = entry.Default,
                ControllingValues = entry.ControllingValues
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            })
            .ToList();

        if (entries.Count == 0)
        {
            foreach (var value in requirement.PicklistValues.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                var parts = value.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var apiValue = parts.Length > 1 ? parts[1] : value.Trim();
                var label = parts.Length > 1
                    ? parts[0]
                    : !string.IsNullOrWhiteSpace(requirement.Label)
                        ? requirement.Label
                        : BuildLabelFromApiValue(apiValue);
                entries.Add(new PicklistValueRequirement { ApiValue = apiValue, Label = label, Default = false });
            }
        }

        if (entries.Count == 0 && !string.IsNullOrWhiteSpace(requirement.Label) && !string.IsNullOrWhiteSpace(requirement.DefaultValue))
        {
            entries.Add(new PicklistValueRequirement { ApiValue = requirement.DefaultValue.Trim(), Label = requirement.Label.Trim(), Default = false });
        }

        return entries;
    }

    private static string AddDependentPicklistValueSettings(string content, SalesforceConfigRequirement requirement, IReadOnlyList<PicklistValueRequirement> entries)
    {
        var sharedControllingValues = ResolveSharedRequestedControllingValues(requirement);
        if (!content.Contains("<controllingField>", StringComparison.OrdinalIgnoreCase))
        {
            return content;
        }

        var proposed = content;
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            var entryControllingValues = entry.ControllingValues.Count > 0
                ? entry.ControllingValues
                : sharedControllingValues;

            foreach (var requestedControlValue in entryControllingValues.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var controllingValue = ResolveExistingControllingValue(proposed, requestedControlValue);
                if (HasValueSetting(proposed, controllingValue, entry.ApiValue))
                {
                    continue;
                }

                builder.AppendLine("        <valueSettings>");
                builder.AppendLine($"            <controllingFieldValue>{EscapeXml(controllingValue)}</controllingFieldValue>");
                builder.AppendLine($"            <valueName>{EscapeXml(entry.ApiValue)}</valueName>");
                builder.AppendLine("        </valueSettings>");
            }
        }

        if (builder.Length == 0)
        {
            return proposed;
        }

        var insertIndex = GetValueSettingsInsertionIndex(proposed);
        if (insertIndex < 0)
        {
            throw new InvalidOperationException("Could not find where to insert dependent picklist valueSettings.");
        }

        return proposed.Insert(insertIndex, builder.ToString());
    }

    private static List<string> ResolveSharedRequestedControllingValues(SalesforceConfigRequirement requirement)
    {
        return ExtractControllingValuesFromText(requirement.Description)
            .Concat(ExtractControllingValuesFromText(requirement.Label))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> ExtractControllingValuesFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (Match match in Regex.Matches(text, @"(?:under|beneath|below|for)\s+(?:the\s+)?(?:controlling\s+)?value\s+[""“']?(?<value>[^""”'\.,;]+)", RegexOptions.IgnoreCase))
        {
            yield return match.Groups["value"].Value.Trim();
        }
    }

    private static string ResolveExistingControllingValue(string content, string requestedValue)
    {
        var existingValues = Regex.Matches(content, @"<controllingFieldValue>(.*?)</controllingFieldValue>", RegexOptions.IgnoreCase | RegexOptions.Singleline)
            .Select(match => match.Groups[1].Value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return existingValues.FirstOrDefault(value => value.Equals(requestedValue, StringComparison.OrdinalIgnoreCase))
               ?? existingValues.FirstOrDefault(value => NormalizeControlValue(value).Equals(NormalizeControlValue(requestedValue), StringComparison.OrdinalIgnoreCase))
               ?? requestedValue;
    }

    private static string NormalizeControlValue(string value)
    {
        var normalized = Regex.Replace(value, @"[^A-Za-z0-9]", string.Empty).ToLowerInvariant();
        return normalized.EndsWith("s", StringComparison.OrdinalIgnoreCase) ? normalized.TrimEnd('s') : normalized;
    }

    private static bool HasValueSetting(string content, string controllingValue, string valueName)
    {
        foreach (Match match in Regex.Matches(content, @"<valueSettings>.*?</valueSettings>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var existingControl = ExtractTagValue(match.Value, "controllingFieldValue");
            var existingValue = ExtractTagValue(match.Value, "valueName");
            if (NormalizeControlValue(existingControl ?? string.Empty).Equals(NormalizeControlValue(controllingValue), StringComparison.OrdinalIgnoreCase)
                && string.Equals(existingValue, valueName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ExtractTagValue(string block, string tagName)
    {
        var match = Regex.Match(block, $@"<{tagName}>(.*?)</{tagName}>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static int GetValueSettingsInsertionIndex(string content)
    {
        var matches = Regex.Matches(content, @"\s*<valueSettings>.*?</valueSettings>\s*", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (matches.Count > 0)
        {
            var last = matches[^1];
            return last.Index + last.Length;
        }

        var valueSetDefinitionEnd = content.IndexOf("</valueSetDefinition>", StringComparison.OrdinalIgnoreCase);
        if (valueSetDefinitionEnd >= 0)
        {
            var nextLineBreak = content.IndexOf('\n', valueSetDefinitionEnd);
            return nextLineBreak >= 0 ? nextLineBreak + 1 : valueSetDefinitionEnd + "</valueSetDefinition>".Length;
        }

        return content.IndexOf("</valueSet>", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildValidationRuleXml(string ruleName, SalesforceConfigRequirement requirement)
    {
        if (string.IsNullOrWhiteSpace(requirement.Formula))
        {
            throw new InvalidOperationException("Validation rule changes require an error condition formula.");
        }

        var errorMessage = string.IsNullOrWhiteSpace(requirement.ErrorMessage)
            ? "Validation rule failed."
            : requirement.ErrorMessage.Trim();

        var location = requirement.ErrorLocation?.Trim() ?? string.Empty;
        var field = requirement.FieldApiName?.Trim() ?? string.Empty;

        // If location is just "Field", it means it's a field-level error but the field name is likely in FieldApiName
        var errorFieldCandidate = location.Equals("Field", StringComparison.OrdinalIgnoreCase)
            ? field
            : FirstNonBlank(location, field);

        var errorField = NormalizeFieldApiName(errorFieldCandidate);

        var builder = new StringBuilder();
        builder.AppendLine(@"<?xml version=""1.0"" encoding=""UTF-8""?>");
        builder.AppendLine(@"<ValidationRule xmlns=""http://soap.sforce.com/2006/04/metadata"">");
        builder.AppendLine($"    <fullName>{ruleName}</fullName>");
        builder.AppendLine("    <active>true</active>");
        builder.AppendLine($"    <errorConditionFormula>{EscapeXml(requirement.Formula.Trim())}</errorConditionFormula>");
        if (!string.IsNullOrWhiteSpace(errorField))
        {
            builder.AppendLine($"    <errorDisplayField>{errorField}</errorDisplayField>");
        }
        builder.AppendLine($"    <errorMessage>{EscapeXml(errorMessage)}</errorMessage>");
        builder.AppendLine("</ValidationRule>");
        return builder.ToString().TrimEnd();
    }

    private static string NormalizeValidationRuleName(string validationRuleName, string label)
    {
        var value = FirstNonBlank(validationRuleName, label);
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return Regex.Replace(value.Trim(), @"[^A-Za-z0-9_]+", "_").Trim('_');
    }

    private static string UpdateTagIfValueProvided(string content, string tagName, string value)
    {
        return string.IsNullOrWhiteSpace(value) ? content : UpsertTag(content, tagName, EscapeXml(value.Trim()));
    }

    private static string UpsertTag(string content, string tagName, string value)
    {
        var pattern = $@"<{tagName}>.*?</{tagName}>";
        if (Regex.IsMatch(content, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            return Regex.Replace(content, pattern, $"<{tagName}>{value}</{tagName}>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        }

        var typeIndex = content.IndexOf("    <type>", StringComparison.OrdinalIgnoreCase);
        if (typeIndex >= 0)
        {
            return content.Insert(typeIndex, $"    <{tagName}>{value}</{tagName}>{Environment.NewLine}");
        }

        var closingIndex = content.LastIndexOf("</CustomField>", StringComparison.OrdinalIgnoreCase);
        return closingIndex < 0
            ? content
            : content.Insert(closingIndex, $"    <{tagName}>{value}</{tagName}>{Environment.NewLine}");
    }

    private static string NormalizeFieldType(string fieldType)
    {
        if (string.IsNullOrWhiteSpace(fieldType))
        {
            return "Text";
        }

        return fieldType.Trim().ToLowerInvariant() switch
        {
            "checkbox" or "boolean" => "Checkbox",
            "textarea" or "longtextarea" or "long text area" => "LongTextArea",
            "picklist" => "Picklist",
            "multiselectpicklist" or "multi-select picklist" or "multi select picklist" => "MultiselectPicklist",
            "number" => "Number",
            "formula" => string.IsNullOrWhiteSpace(fieldType) ? "Text" : FirstNonBlank(fieldType, "Text"),
            _ => "Text"
        };
    }

    private static string NormalizeCheckboxDefault(string defaultValue)
    {
        return string.IsNullOrWhiteSpace(defaultValue)
            ? "false"
            : defaultValue.Trim().ToLowerInvariant();
    }

    private static string NormalizeObjectApiName(string objectApiName)
    {
        var normalized = NormalizeCustomApiName(objectApiName);
        return normalized.Equals("Candidate__c", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("Candidate", StringComparison.OrdinalIgnoreCase)
            ? "Contact"
            : normalized;
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

    private static string BuildLabelFromApiName(string fieldApiName)
    {
        var suffix = GetCustomFieldSuffix(fieldApiName);
        var name = !string.IsNullOrWhiteSpace(suffix)
            ? fieldApiName[..^suffix.Length]
            : fieldApiName;

        return string.Join(" ", name.Split('_', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool TryResolveExistingFieldPath(string repoPath, string objectApiName, string requestedFieldApiName, out string relativePath, out string resolvedFieldApiName)
    {
        relativePath = string.Empty;
        resolvedFieldApiName = string.Empty;
        if (requestedFieldApiName.EndsWith("__pc", StringComparison.OrdinalIgnoreCase)
            && TryResolvePersonAccountBackingContactFieldPath(repoPath, requestedFieldApiName, out relativePath, out resolvedFieldApiName))
        {
            return true;
        }

        var fieldsDirectory = Path.Combine(repoPath, "force-app", "main", "default", "objects", objectApiName, "fields");
        if (!Directory.Exists(fieldsDirectory))
        {
            return false;
        }

        var requestedLookupName = NormalizeFieldLookupName(requestedFieldApiName);
        var match = Directory
            .GetFiles(fieldsDirectory, "*.field-meta.xml")
            .FirstOrDefault(path => NormalizeFieldLookupName(Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path))).Equals(requestedLookupName, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            return false;
        }

        resolvedFieldApiName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(match));
        relativePath = Path.Combine("force-app", "main", "default", "objects", objectApiName, "fields", $"{resolvedFieldApiName}.field-meta.xml");
        return true;
    }

    private static bool TryResolvePersonAccountBackingContactFieldPath(string repoPath, string requestedFieldApiName, out string relativePath, out string resolvedFieldApiName)
    {
        relativePath = string.Empty;
        resolvedFieldApiName = string.Empty;

        var contactFieldApiName = requestedFieldApiName[..^4] + "__c";
        var contactFieldsDirectory = Path.Combine(repoPath, "force-app", "main", "default", "objects", "Contact", "fields");
        if (!Directory.Exists(contactFieldsDirectory))
        {
            return false;
        }

        var exactPath = Path.Combine(contactFieldsDirectory, $"{contactFieldApiName}.field-meta.xml");
        var match = File.Exists(exactPath)
            ? exactPath
            : Directory
                .GetFiles(contactFieldsDirectory, "*.field-meta.xml")
                .FirstOrDefault(path => NormalizeFieldLookupName(Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path))).Equals(NormalizeFieldLookupName(contactFieldApiName), StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            return false;
        }

        resolvedFieldApiName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(match));
        relativePath = Path.Combine("force-app", "main", "default", "objects", "Contact", "fields", $"{resolvedFieldApiName}.field-meta.xml");
        return true;
    }

    private static string NormalizeFieldLookupName(string fieldApiName)
    {
        var value = fieldApiName.Trim();
        var suffix = GetCustomFieldSuffix(value);
        if (!string.IsNullOrWhiteSpace(suffix))
        {
            value = value[..^suffix.Length];
        }

        return Regex.Replace(value, @"[\s_\-\.]", string.Empty).ToLowerInvariant();
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

    private static string BuildLabelFromApiValue(string apiValue)
    {
        return string.Join(" ", apiValue.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private static string BuildApiValueFromLabel(string label)
    {
        return Regex.Replace(label.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
    }

    private static string FirstNonBlank(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static string DetectIndent(string content, int index)
    {
        var lineStart = GetLineStart(content, index);
        var line = content[lineStart..Math.Min(index, content.Length)];
        return new string(line.TakeWhile(char.IsWhiteSpace).ToArray());
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

    private static string EscapeXml(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    private sealed record PicklistValueMatch(string ApiValue, int StartIndex, int Length, string Indent);
}


