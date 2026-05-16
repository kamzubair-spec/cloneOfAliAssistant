using System.Text.RegularExpressions;
using System.Xml.Linq;
using eZBERP_AI_IDE.Models;
using File = System.IO.File;

namespace eZBERP_AI_IDE.Services;

public sealed class LabelManagementService : IRepositoryAwareConfigWorkItemHandler
{
    public string ServiceName => nameof(LabelManagementService);

    public bool CanHandle(SalesforceConfigRequirement requirement)
    {
        return requirement.Type.Equals("custom_label", StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(GetLabelName(requirement))
               && !string.IsNullOrWhiteSpace(GetLabelValue(requirement));
    }

    public bool CanHandle(string repoPath, SalesforceConfigRequirement requirement)
    {
        return CanHandle(requirement) && File.Exists(GetLabelsPath(repoPath));
    }

    public string BuildCannotHandleReason(string repoPath, SalesforceConfigRequirement requirement)
    {
        if (!File.Exists(GetLabelsPath(repoPath)))
        {
            return "No CustomLabels.labels-meta.xml file was found.";
        }

        if (string.IsNullOrWhiteSpace(GetLabelName(requirement)) || string.IsNullOrWhiteSpace(GetLabelValue(requirement)))
        {
            return "Custom label changes need a label API name and value.";
        }

        return "Custom label requirement is supported.";
    }

    public async Task<FileChangeSet?> BuildChangeSetAsync(string repoPath, SalesforceConfigRequirement requirement)
    {
        var path = GetLabelsPath(repoPath);
        var existing = await File.ReadAllTextAsync(path);
        var proposed = UpsertLabel(existing, requirement);
        return new FileChangeSet(
            $"Custom label metadata change for {GetLabelName(requirement)}",
            new[] { new FileChangeProposal(Path.GetRelativePath(repoPath, path), existing, proposed, true) });
    }

    private static string UpsertLabel(string content, SalesforceConfigRequirement requirement)
    {
        var document = XDocument.Parse(content, LoadOptions.PreserveWhitespace);
        var ns = document.Root?.Name.Namespace ?? XNamespace.None;
        var name = NormalizeMetadataName(GetLabelName(requirement));
        var value = GetLabelValue(requirement);
        var label = document.Descendants(ns + "labels")
            .FirstOrDefault(item => item.Element(ns + "fullName")?.Value.Equals(name, StringComparison.OrdinalIgnoreCase) == true);

        if (label is null)
        {
            label = new XElement(ns + "labels",
                new XElement(ns + "fullName", name),
                new XElement(ns + "categories", string.Empty),
                new XElement(ns + "language", "en_US"),
                new XElement(ns + "protected", "false"),
                new XElement(ns + "shortDescription", FirstNonBlank(requirement.Description, requirement.Label, name)),
                new XElement(ns + "value", value));
            document.Root?.Add(label);
        }
        else
        {
            UpsertChild(label, ns, "value", value);
            if (!string.IsNullOrWhiteSpace(requirement.Description))
            {
                UpsertChild(label, ns, "shortDescription", requirement.Description.Trim());
            }
        }

        return Serialize(document, content);
    }

    private static void UpsertChild(XElement parent, XNamespace ns, string name, string value)
    {
        var element = parent.Element(ns + name);
        if (element is null)
        {
            parent.Add(new XElement(ns + name, value));
        }
        else
        {
            element.Value = value;
        }
    }

    private static string GetLabelsPath(string repoPath) => Path.Combine(repoPath, "force-app", "main", "default", "labels", "CustomLabels.labels-meta.xml");
    private static string GetLabelName(SalesforceConfigRequirement requirement) => FirstNonBlank(requirement.TargetMetadataName, requirement.FieldApiName, requirement.Label);
    private static string GetLabelValue(SalesforceConfigRequirement requirement) => FirstNonBlank(requirement.DefaultValue, requirement.Description);
    private static string NormalizeMetadataName(string value) => Regex.Replace(value.Trim(), @"[^A-Za-z0-9_]+", "_").Trim('_');
    private static string FirstNonBlank(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    private static string Serialize(XDocument document, string originalContent)
    {
        var declaration = document.Declaration?.ToString();
        var body = document.ToString(SaveOptions.DisableFormatting);
        var lineEnding = originalContent.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return string.IsNullOrWhiteSpace(declaration) ? body : declaration + lineEnding + body;
    }
}
