using System.Xml.Linq;
using eZBERP_AI_IDE.Models;

namespace eZBERP_AI_IDE.Services;

internal sealed class SalesforceFieldEditingToolkit
{
    private static readonly XNamespace Ns = "http://soap.sforce.com/2006/04/metadata";
    private readonly MetadataDiscoveryService _metadataDiscoveryService = new();

    internal string GetFieldPath(string repoPath, SalesforceConfigRequirement requirement)
    {
        return Path.Combine(repoPath, "force-app", "main", "default", "objects", requirement.ObjectApiName, "fields", $"{requirement.FieldApiName}.field-meta.xml");
    }

    internal string? GetExistingFieldType(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var doc = XDocument.Load(path);
        return doc.Root?.Element(Ns + "type")?.Value ?? string.Empty;
    }

    internal string BuildFieldContent(string repoPath, SalesforceConfigRequirement requirement, string? existingContent = null)
    {
        var fieldType = FieldMetadataCatalog.NormalizeFieldType(requirement.FieldType);
        var doc = string.IsNullOrWhiteSpace(existingContent)
            ? new XDocument(new XDeclaration("1.0", "UTF-8", null), new XElement(Ns + "CustomField"))
            : XDocument.Parse(existingContent);

        var root = doc.Root!;
        SetOrReplace(root, "fullName", requirement.FieldApiName);
        SetOrReplace(root, "label", FirstNonBlank(requirement.Label, requirement.FieldApiName));
        SetOrReplaceIfNotBlank(root, "description", requirement.FieldDescription);
        SetOrReplaceIfNotBlank(root, "inlineHelpText", requirement.InlineHelpText);
        if (requirement.Required.HasValue && fieldType is not "masterdetail")
        {
            SetOrReplace(root, "required", requirement.Required.Value.ToString().ToLowerInvariant());
        }
        SetOrReplace(root, "type", NormalizeTypeElementValue(fieldType));

        switch (fieldType)
        {
            case "text":
                SetOrReplace(root, "length", (requirement.Length ?? 255).ToString());
                break;
            case "textarea":
            case "longtextarea":
                SetOrReplace(root, "length", (requirement.Length ?? 32768).ToString());
                SetOrReplace(root, "visibleLines", (requirement.VisibleLines ?? 3).ToString());
                break;
            case "number":
            case "currency":
            case "percent":
                SetOrReplace(root, "precision", (requirement.Precision ?? 18).ToString());
                SetOrReplace(root, "scale", (requirement.Scale ?? 0).ToString());
                break;
            case "checkbox":
                SetOrReplace(root, "defaultValue", string.IsNullOrWhiteSpace(requirement.DefaultValue) ? "false" : requirement.DefaultValue.ToLowerInvariant());
                break;
            case "picklist":
            case "multiselectpicklist":
                BuildPicklist(root, requirement);
                if (fieldType == "multiselectpicklist")
                {
                    SetOrReplace(root, "visibleLines", (requirement.VisibleLines ?? 4).ToString());
                }
                break;
            case "lookup":
            case "masterdetail":
                BuildRelationship(root, requirement, fieldType);
                break;
        }

        SetOrReplace(root, "trackTrending", "false");
        return doc.Declaration + Environment.NewLine + root.ToString();
    }

    internal List<FileChangeProposal> BuildRecordTypeProposals(string repoPath, SalesforceConfigRequirement requirement)
    {
        var proposals = new List<FileChangeProposal>();
        if (requirement.RecordTypeNames.Count == 0 || !IsPicklist(requirement))
        {
            return proposals;
        }

        var values = requirement.PicklistEntries.Count > 0
            ? requirement.PicklistEntries.Select(item => item.ApiValue).Where(value => !string.IsNullOrWhiteSpace(value)).ToList()
            : requirement.PicklistValues.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();

        if (values.Count == 0 && !string.IsNullOrWhiteSpace(requirement.GlobalValueSetName))
        {
            values = _metadataDiscoveryService.GetGlobalValueSetValues(repoPath, requirement.GlobalValueSetName);
        }

        foreach (var recordTypeName in requirement.RecordTypeNames)
        {
            var path = Path.Combine(repoPath, "force-app", "main", "default", "objects", requirement.ObjectApiName, "recordTypes", $"{recordTypeName}.recordType-meta.xml");
            if (!File.Exists(path))
            {
                continue;
            }

            var existingContent = File.ReadAllText(path);
            var doc = XDocument.Parse(existingContent);
            var root = doc.Root!;
            var existingNode = root.Elements(Ns + "picklistValues")
                .FirstOrDefault(node => string.Equals(node.Element(Ns + "picklist")?.Value, requirement.FieldApiName, StringComparison.OrdinalIgnoreCase));

            var newNode = new XElement(Ns + "picklistValues",
                new XElement(Ns + "picklist", requirement.FieldApiName));
            foreach (var value in values.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                newNode.Add(new XElement(Ns + "values",
                    new XElement(Ns + "fullName", value),
                    new XElement(Ns + "default", "false")));
            }

            if (existingNode is null)
            {
                root.Add(newNode);
            }
            else
            {
                existingNode.ReplaceWith(newNode);
            }

            proposals.Add(new FileChangeProposal(
                Path.GetRelativePath(repoPath, path),
                existingContent,
                doc.Declaration + Environment.NewLine + root.ToString(),
                true));
        }

        return proposals;
    }

    private static void BuildPicklist(XElement root, SalesforceConfigRequirement requirement)
    {
        root.Elements(Ns + "valueSet").Remove();

        var valueSet = new XElement(Ns + "valueSet");
        if (!string.IsNullOrWhiteSpace(requirement.ControllingFieldApiName))
        {
            valueSet.Add(new XElement(Ns + "controllingField", requirement.ControllingFieldApiName));
        }
        valueSet.Add(new XElement(Ns + "restricted", "true"));

        if (requirement.ValueSetSource.Equals("global", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(requirement.GlobalValueSetName))
        {
            valueSet.Add(new XElement(Ns + "valueSetName", requirement.GlobalValueSetName));
        }
        else
        {
            var definition = new XElement(Ns + "valueSetDefinition",
                new XElement(Ns + "sorted", requirement.KeepPicklistValuesInOrder.ToString().ToLowerInvariant()));

            var entries = requirement.PicklistEntries.Count > 0
                ? requirement.PicklistEntries
                : requirement.PicklistValues.Select(value => new PicklistValueRequirement { ApiValue = value, Label = value }).ToList();

            foreach (var entry in entries.Where(entry => !string.IsNullOrWhiteSpace(entry.ApiValue)))
            {
                definition.Add(new XElement(Ns + "value",
                    new XElement(Ns + "fullName", entry.ApiValue),
                    new XElement(Ns + "default", entry.Default.ToString().ToLowerInvariant()),
                    new XElement(Ns + "label", FirstNonBlank(entry.Label, entry.ApiValue))));
            }

            valueSet.Add(definition);
        }

        foreach (var entry in requirement.PicklistEntries.Where(entry => entry.ControllingValues.Count > 0))
        {
            var valueSettings = new XElement(Ns + "valueSettings");
            foreach (var controllingValue in entry.ControllingValues)
            {
                valueSettings.Add(new XElement(Ns + "controllingFieldValue", controllingValue));
            }
            valueSettings.Add(new XElement(Ns + "valueName", entry.ApiValue));
            valueSet.Add(valueSettings);
        }

        root.Add(valueSet);
    }

    private static void BuildRelationship(XElement root, SalesforceConfigRequirement requirement, string fieldType)
    {
        SetOrReplace(root, "referenceTo", requirement.RelationshipTargetObject);
        SetOrReplaceIfNotBlank(root, "relationshipLabel", FirstNonBlank(requirement.RelationshipLabel, requirement.Label));
        SetOrReplaceIfNotBlank(root, "relationshipName", FirstNonBlank(requirement.RelationshipName, requirement.FieldApiName.Replace("__c", string.Empty, StringComparison.OrdinalIgnoreCase)));

        if (fieldType == "lookup")
        {
            SetOrReplace(root, "required", requirement.Required.GetValueOrDefault().ToString().ToLowerInvariant());
        }
        else
        {
            SetOrReplace(root, "reparentableMasterDetail", "false");
            SetOrReplace(root, "writeRequiresMasterRead", "false");
        }
    }

    private static bool IsPicklist(SalesforceConfigRequirement requirement)
    {
        var fieldType = FieldMetadataCatalog.NormalizeFieldType(requirement.FieldType);
        return fieldType is "picklist" or "multiselectpicklist";
    }

    private static string NormalizeTypeElementValue(string fieldType)
    {
        return fieldType switch
        {
            "longtextarea" => "LongTextArea",
            "multiselectpicklist" => "MultiselectPicklist",
            "masterdetail" => "MasterDetail",
            "datetime" => "DateTime",
            _ => char.ToUpperInvariant(fieldType[0]) + fieldType[1..]
        };
    }

    private static void SetOrReplace(XElement root, string elementName, string value)
    {
        var existing = root.Element(Ns + elementName);
        if (existing is null)
        {
            root.Add(new XElement(Ns + elementName, value));
        }
        else
        {
            existing.Value = value;
        }
    }

    private static void SetOrReplaceIfNotBlank(XElement root, string elementName, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            SetOrReplace(root, elementName, value);
        }
    }

    private static string FirstNonBlank(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }
}
