using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using eZBERP_AI_IDE.Models;
using File = System.IO.File;

namespace eZBERP_AI_IDE.Services;

public sealed class CustomMetadataManagementService : IConfigWorkItemHandler, IRepositoryAwareConfigWorkItemHandler
{
    private static readonly XNamespace MetadataNs = "http://soap.sforce.com/2006/04/metadata";
    private static readonly XNamespace XsiNs = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly XNamespace XsdNs = "http://www.w3.org/2001/XMLSchema";

    public string ServiceName => nameof(CustomMetadataManagementService);

    public bool CanHandle(SalesforceConfigRequirement requirement)
    {
        return requirement.Type.Equals("custom_metadata", StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(GetTypeName(requirement))
               && !string.IsNullOrWhiteSpace(GetRecordDeveloperName(requirement))
               && requirement.CustomMetadataValues.Count > 0;
    }

    public bool CanHandle(string repoPath, SalesforceConfigRequirement requirement)
    {
        return CanHandle(requirement) && Directory.Exists(Path.Combine(repoPath, "force-app", "main", "default", "customMetadata"));
    }

    public string BuildCannotHandleReason(string repoPath, SalesforceConfigRequirement requirement)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(GetTypeName(requirement)))
        {
            missing.Add("custom metadata type API name");
        }

        if (string.IsNullOrWhiteSpace(GetRecordDeveloperName(requirement)))
        {
            missing.Add("record developer name or label");
        }

        if (requirement.CustomMetadataValues.Count == 0)
        {
            missing.Add("field/value pairs");
        }

        var customMetadataFolder = Path.Combine(repoPath, "force-app", "main", "default", "customMetadata");
        if (!Directory.Exists(customMetadataFolder))
        {
            missing.Add("customMetadata folder");
        }

        return missing.Count == 0
            ? "Custom metadata support exists, but this requirement could not be matched to CustomMetadataManagementService."
            : $"Custom metadata support exists, but the extracted requirement is missing: {string.Join(", ", missing)}.";
    }

    public async Task<FileChangeSet?> BuildChangeSetAsync(string repoPath, SalesforceConfigRequirement requirement)
    {
        var typeName = NormalizeCustomMetadataTypeName(GetTypeName(requirement));
        var developerName = NormalizeDeveloperName(GetRecordDeveloperName(requirement));
        var relativePath = Path.Combine("force-app", "main", "default", "customMetadata", $"{typeName}.{developerName}.md-meta.xml");
        var path = Path.Combine(repoPath, relativePath);
        var existing = File.Exists(path) ? await File.ReadAllTextAsync(path) : string.Empty;
        var proposed = File.Exists(path)
            ? UpdateExistingXml(existing, requirement)
            : BuildNewXml(requirement);

        return new FileChangeSet(
            $"Custom metadata record change for {typeName}.{developerName}",
            new[] { new FileChangeProposal(relativePath, existing, proposed, File.Exists(path)) });
    }

    private static string BuildNewXml(SalesforceConfigRequirement requirement)
    {
        var label = FirstNonBlank(requirement.Label, GetRecordDeveloperName(requirement).Replace("_", " "));
        var builder = new StringBuilder();
        builder.AppendLine(@"<?xml version=""1.0"" encoding=""UTF-8""?>");
        builder.AppendLine(@"<CustomMetadata xmlns=""http://soap.sforce.com/2006/04/metadata"" xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"">");
        builder.AppendLine($"    <label>{EscapeXml(label)}</label>");
        builder.AppendLine("    <protected>false</protected>");
        foreach (var item in NormalizeValues(requirement.CustomMetadataValues))
        {
            builder.AppendLine("    <values>");
            builder.AppendLine($"        <field>{EscapeXml(item.Key)}</field>");
            builder.AppendLine($"        <value xsi:type=\"{GetXsdType(item.Value)}\">{EscapeXml(item.Value)}</value>");
            builder.AppendLine("    </values>");
        }

        builder.AppendLine("</CustomMetadata>");
        return builder.ToString().TrimEnd();
    }

    private static string UpdateExistingXml(string existing, SalesforceConfigRequirement requirement)
    {
        var document = XDocument.Parse(existing, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidOperationException("Custom metadata XML has no root element.");

        var label = FirstNonBlank(requirement.Label);
        if (!string.IsNullOrWhiteSpace(label))
        {
            var labelElement = root.Element(MetadataNs + "label");
            if (labelElement is null)
            {
                root.AddFirst(new XElement(MetadataNs + "label", label));
            }
            else
            {
                labelElement.Value = label;
            }
        }

        foreach (var item in NormalizeValues(requirement.CustomMetadataValues))
        {
            UpsertValue(root, item.Key, item.Value);
        }

        return document.Declaration is null
            ? document.ToString()
            : $"{document.Declaration}{Environment.NewLine}{document}";
    }

    private static void UpsertValue(XElement root, string fieldName, string value)
    {
        var valuesElement = root.Elements(MetadataNs + "values")
            .FirstOrDefault(element => string.Equals(element.Element(MetadataNs + "field")?.Value, fieldName, StringComparison.OrdinalIgnoreCase));

        if (valuesElement is null)
        {
            root.Add(
                new XElement(MetadataNs + "values",
                    new XElement(MetadataNs + "field", fieldName),
                    new XElement(MetadataNs + "value",
                        new XAttribute(XsiNs + "type", GetXsdType(value)),
                        value)));
            return;
        }

        var valueElement = valuesElement.Element(MetadataNs + "value");
        if (valueElement is null)
        {
            valuesElement.Add(new XElement(MetadataNs + "value", new XAttribute(XsiNs + "type", GetXsdType(value)), value));
        }
        else
        {
            valueElement.SetAttributeValue(XsiNs + "type", GetXsdType(value));
            valueElement.Value = value;
        }
    }

    private static Dictionary<string, string> NormalizeValues(Dictionary<string, string> values)
    {
        return values
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .ToDictionary(
                item => NormalizeCustomFieldName(item.Key),
                item => item.Value?.Trim() ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
    }

    private static string GetTypeName(SalesforceConfigRequirement requirement)
    {
        return FirstNonBlank(requirement.CustomMetadataTypeApiName, requirement.ObjectApiName, requirement.TargetMetadataName);
    }

    private static string GetRecordDeveloperName(SalesforceConfigRequirement requirement)
    {
        return FirstNonBlank(requirement.RecordDeveloperName, requirement.Label);
    }

    private static string NormalizeCustomMetadataTypeName(string value)
    {
        value = value.Trim();
        return value.EndsWith("__mdt", StringComparison.OrdinalIgnoreCase) ? value[..^5] : value;
    }

    private static string NormalizeDeveloperName(string value)
    {
        return Regex.Replace(value.Trim(), @"[^A-Za-z0-9_]+", "_").Trim('_');
    }

    private static string NormalizeCustomFieldName(string value)
    {
        value = value.Trim();
        if (value.EndsWith("__c", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var apiName = Regex.Replace(value, @"[^A-Za-z0-9]+", "_").Trim('_');
        return apiName.EndsWith("__c", StringComparison.OrdinalIgnoreCase) ? apiName : $"{apiName}__c";
    }

    private static string GetXsdType(string value)
    {
        if (bool.TryParse(value, out _))
        {
            return "xsd:boolean";
        }

        return decimal.TryParse(value, out _) ? "xsd:double" : "xsd:string";
    }

    private static string FirstNonBlank(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string EscapeXml(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }
}
