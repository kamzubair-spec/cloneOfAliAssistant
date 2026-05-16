using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using File = System.IO.File;

namespace eZBERP_AI_IDE.Services;

public sealed class MetadataPlanService
{
    private readonly DeepSeekClient _deepSeekClient;
    private readonly ProfileFlsToolService _profileFlsToolService;

    public MetadataPlanService(DeepSeekClient deepSeekClient, ProfileFlsToolService profileFlsToolService)
    {
        _deepSeekClient = deepSeekClient;
        _profileFlsToolService = profileFlsToolService;
    }

    public bool IsMetadataPlanRequest(string userCommand)
    {
        var lowered = userCommand.ToLowerInvariant();
        return (lowered.Contains("field") && (lowered.Contains("__c") || lowered.Contains("object")))
               || lowered.Contains("fls")
               || lowered.Contains("field level security")
               || lowered.Contains("profile");
    }

    public async Task<FileChangeSet?> BuildChangeSetAsync(string repoPath, string userCommand)
    {
        var plan = await ExtractPlanAsync(repoPath, userCommand);
        if ((plan.Fields.Count == 0) && (plan.ProfileFls.Count == 0))
        {
            return null;
        }

        EnsureRequestedNewFieldsDoNotAlreadyExist(repoPath, userCommand, plan);

        var changeSets = new List<FileChangeSet>();

        if (plan.Fields.Count > 0)
        {
            changeSets.Add(await BuildFieldChangeSetAsync(repoPath, plan.Fields));
        }

        foreach (var fls in plan.ProfileFls)
        {
            var editableProfiles = fls.Profiles
                .Where(profile => profile.Readable && profile.Editable)
                .Select(profile => profile.Name)
                .ToList();

            var readOnlyProfiles = fls.Profiles
                .Where(profile => profile.Readable && !profile.Editable)
                .Select(profile => profile.Name)
                .ToList();

            if (editableProfiles.Count == 0 && readOnlyProfiles.Count == 0 && !fls.ApplyReadOnlyToRemainingProfiles)
            {
                continue;
            }

            changeSets.Add(await _profileFlsToolService.BuildChangeSetAsync(
                repoPath,
                new ProfileFlsRequest(
                    fls.ObjectApiName,
                    fls.FieldApiName,
                    editableProfiles,
                    readOnlyProfiles,
                    fls.ApplyReadOnlyToRemainingProfiles)));
        }

        var proposals = changeSets.SelectMany(changeSet => changeSet.Files).ToList();
        return proposals.Count == 0
            ? null
            : new FileChangeSet("AI extracted metadata plan", proposals);
    }

    private static void EnsureRequestedNewFieldsDoNotAlreadyExist(string repoPath, string userCommand, MetadataPlan plan)
    {
        if (!IsFieldCreationRequest(userCommand))
        {
            return;
        }

        var duplicates = plan.Fields
            .Where(field => FieldMetadataExists(repoPath, field.ObjectApiName, field.FieldApiName))
            .Select(field => $"{field.ObjectApiName}.{field.FieldApiName}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (duplicates.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "The requested field already exists, so no metadata changes were proposed: "
            + string.Join(", ", duplicates)
            + ". If you want to update FLS for the existing field, ask for an FLS update only.");
    }

    private static bool IsFieldCreationRequest(string userCommand)
    {
        return Regex.IsMatch(
            userCommand,
            @"\b(add|create|new)\b.{0,80}\b(field|checkbox|text|number|date|picklist)\b|\b(field|checkbox|text|number|date|picklist)\b.{0,80}\b(add|create|new)\b",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }

    private static bool FieldMetadataExists(string repoPath, string objectApiName, string fieldApiName)
    {
        if (string.IsNullOrWhiteSpace(objectApiName) || string.IsNullOrWhiteSpace(fieldApiName))
        {
            return false;
        }

        var fieldPath = Path.Combine(
            repoPath,
            "force-app",
            "main",
            "default",
            "objects",
            objectApiName,
            "fields",
            $"{fieldApiName}.field-meta.xml");

        return File.Exists(fieldPath);
    }
    private async Task<MetadataPlan> ExtractPlanAsync(string repoPath, string userCommand)
    {
        var availableProfiles = GetAvailableProfiles(repoPath);
        var availableObjects = GetAvailableObjects(repoPath);

        var systemPrompt = $$"""
        You extract Salesforce metadata intent from user requests.
        Return JSON only. No markdown. No explanations.

        Available profile file names:
        {{string.Join("\n", availableProfiles)}}

        Available object API names:
        {{string.Join("\n", availableObjects)}}

        Output schema:
        {
          "fields": [
            {
              "objectApiName": "Placement__c",
              "fieldApiName": "Test_Sync__c",
              "type": "Checkbox|Text|Number|Date|DateTime|LongTextArea|Picklist",
              "label": "Test Sync",
              "inlineHelpText": "help text or tooltip",
              "description": "optional description",
              "length": 255,
              "defaultValue": "false",
              "required": false
            }
          ],
          "profileFls": [
            {
              "objectApiName": "Placement__c",
              "fieldApiName": "Test_Sync__c",
              "profiles": [
                { "name": "Admin.profile-meta.xml", "readable": true, "editable": true },
                { "name": "LargeStaff.profile-meta.xml", "readable": true, "editable": false }
              ],
              "applyReadOnlyToRemainingProfiles": false
            }
          ]
        }

        Rules:
        - Interpret typos and casual wording, but output only concrete requested metadata changes.
        - If the user lists specific profiles for Read/Write, only those profiles get editable=true.
        - If the user lists specific profiles for Read Only/Readonly/Readyonly, those profiles get readable=true and editable=false.
        - Do not add all other profiles unless the user explicitly asks for other/remaining/all other profiles.
        - Use existing profile file names from the available list when possible.
        - If FLS is requested for an existing field, include profileFls even if no new field is requested.
        - Do not invent layouts, permission sets, classes, triggers, or unrelated changes.
        - Normalize custom object/field API casing to Salesforce style, for example placement__c -> Placement__c and test_sync -> Test_Sync__c.
        """;

        var response = await _deepSeekClient.SendChatAsync(DeepSeekModels.Config, systemPrompt, userCommand, 0.0, 2500);
        var json = ExtractJson(response);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var plan = JsonSerializer.Deserialize<MetadataPlan>(json, options) ?? new MetadataPlan();

        return NormalizeAndValidatePlan(repoPath, plan);
    }

    private static MetadataPlan NormalizeAndValidatePlan(string repoPath, MetadataPlan plan)
    {
        var normalized = new MetadataPlan();

        foreach (var field in plan.Fields)
        {
            var objectApiName = ResolveObjectApiName(repoPath, field.ObjectApiName);
            var fieldApiName = NormalizeFieldApiName(field.FieldApiName);
            if (string.IsNullOrWhiteSpace(objectApiName) || string.IsNullOrWhiteSpace(fieldApiName))
            {
                continue;
            }

            normalized.Fields.Add(field with
            {
                ObjectApiName = objectApiName,
                FieldApiName = fieldApiName,
                Type = NormalizeFieldType(field.Type),
                Label = string.IsNullOrWhiteSpace(field.Label) ? BuildLabel(fieldApiName) : field.Label.Trim(),
                InlineHelpText = field.InlineHelpText?.Trim(),
                Description = field.Description?.Trim()
            });
        }

        foreach (var fls in plan.ProfileFls)
        {
            var objectApiName = ResolveObjectApiName(repoPath, fls.ObjectApiName);
            var fieldApiName = NormalizeFieldApiName(fls.FieldApiName);
            if (string.IsNullOrWhiteSpace(objectApiName) || string.IsNullOrWhiteSpace(fieldApiName))
            {
                continue;
            }

            var profiles = fls.Profiles
                .Where(profile => !string.IsNullOrWhiteSpace(profile.Name))
                .Select(profile => profile with { Name = profile.Name.Trim() })
                .ToList();

            normalized.ProfileFls.Add(fls with
            {
                ObjectApiName = objectApiName,
                FieldApiName = fieldApiName,
                Profiles = profiles,
                ApplyReadOnlyToRemainingProfiles = fls.ApplyReadOnlyToRemainingProfiles
            });
        }

        return normalized;
    }

    private static async Task<FileChangeSet> BuildFieldChangeSetAsync(string repoPath, IEnumerable<MetadataFieldPlan> fields)
    {
        var proposals = new List<FileChangeProposal>();

        foreach (var field in fields)
        {
            var objectDirectory = Path.Combine(repoPath, "force-app", "main", "default", "objects", field.ObjectApiName);
            if (!Directory.Exists(objectDirectory))
            {
                throw new DirectoryNotFoundException($"Object directory was not found: {objectDirectory}");
            }

            var relativePath = Path.Combine("force-app", "main", "default", "objects", field.ObjectApiName, "fields", $"{field.FieldApiName}.field-meta.xml");
            var fullPath = Path.Combine(repoPath, relativePath);
            var existingContent = File.Exists(fullPath) ? await File.ReadAllTextAsync(fullPath) : string.Empty;
            var proposedContent = BuildFieldXml(field);

            proposals.Add(new FileChangeProposal(
                relativePath,
                existingContent,
                proposedContent,
                !string.IsNullOrEmpty(existingContent)));
        }

        return new FileChangeSet("Field metadata update", proposals);
    }

    private static string BuildFieldXml(MetadataFieldPlan field)
    {
        XNamespace ns = "http://soap.sforce.com/2006/04/metadata";
        var root = new XElement(ns + "CustomField",
            new XElement(ns + "fullName", field.FieldApiName));

        if (field.Type.Equals("Checkbox", StringComparison.OrdinalIgnoreCase))
        {
            root.Add(new XElement(ns + "defaultValue", string.IsNullOrWhiteSpace(field.DefaultValue) ? "false" : field.DefaultValue));
        }

        if (!string.IsNullOrWhiteSpace(field.Description))
        {
            root.Add(new XElement(ns + "description", field.Description));
        }

        root.Add(new XElement(ns + "externalId", "false"));

        if (!string.IsNullOrWhiteSpace(field.InlineHelpText))
        {
            root.Add(new XElement(ns + "inlineHelpText", field.InlineHelpText));
        }

        root.Add(new XElement(ns + "label", field.Label));

        if (field.Type.Equals("Text", StringComparison.OrdinalIgnoreCase))
        {
            root.Add(new XElement(ns + "length", field.Length ?? 255));
            root.Add(new XElement(ns + "required", (field.Required ?? false).ToString().ToLowerInvariant()));
        }

        root.Add(
            new XElement(ns + "trackFeedHistory", "false"),
            new XElement(ns + "trackHistory", "false"),
            new XElement(ns + "trackTrending", "false"),
            new XElement(ns + "type", field.Type));

        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
        using var writer = new Utf8StringWriter();
        document.Save(writer, SaveOptions.None);
        return writer.ToString();
    }

    private static string ExtractJson(string response)
    {
        var trimmed = response.Trim();
        trimmed = Regex.Replace(trimmed, @"^```(?:json)?\s*", string.Empty, RegexOptions.IgnoreCase);
        trimmed = Regex.Replace(trimmed, @"\s*```$", string.Empty);

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            throw new InvalidOperationException("The AI did not return a valid metadata JSON plan.");
        }

        return trimmed[start..(end + 1)];
    }

    private static IReadOnlyList<string> GetAvailableProfiles(string repoPath)
    {
        var profilesDirectory = Path.Combine(repoPath, "force-app", "main", "default", "profiles");
        return Directory.Exists(profilesDirectory)
            ? Directory.GetFiles(profilesDirectory, "*.profile-meta.xml").Select(Path.GetFileName).Where(name => name is not null).Cast<string>().OrderBy(name => name).ToList()
            : Array.Empty<string>();
    }

    private static IReadOnlyList<string> GetAvailableObjects(string repoPath)
    {
        var objectsDirectory = Path.Combine(repoPath, "force-app", "main", "default", "objects");
        return Directory.Exists(objectsDirectory)
            ? Directory.GetDirectories(objectsDirectory).Select(Path.GetFileName).Where(name => name is not null).Cast<string>().OrderBy(name => name).ToList()
            : Array.Empty<string>();
    }

    private static string ResolveObjectApiName(string repoPath, string objectApiName)
    {
        if (string.IsNullOrWhiteSpace(objectApiName))
        {
            return string.Empty;
        }

        var normalizedInput = NormalizeToken(objectApiName);
        var objectsDirectory = Path.Combine(repoPath, "force-app", "main", "default", "objects");
        if (Directory.Exists(objectsDirectory))
        {
            foreach (var directory in Directory.GetDirectories(objectsDirectory))
            {
                var name = Path.GetFileName(directory);
                if (NormalizeToken(name) == normalizedInput)
                {
                    return name;
                }
            }
        }

        if (normalizedInput == NormalizeToken("placemet__c"))
        {
            return "Placement__c";
        }

        return NormalizeCustomApiName(objectApiName);
    }

    private static string NormalizeFieldApiName(string fieldApiName)
    {
        if (string.IsNullOrWhiteSpace(fieldApiName))
        {
            return string.Empty;
        }

        var value = fieldApiName.Trim();
        if (value.Contains('.'))
        {
            value = value.Split('.').Last();
        }

        if (value.StartsWith("Field ", StringComparison.OrdinalIgnoreCase))
        {
            value = value["Field ".Length..].Trim();
        }

        if (string.IsNullOrWhiteSpace(value) || value.Equals("Field", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (value.EndsWith("__c", StringComparison.OrdinalIgnoreCase))
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
        if (!value.EndsWith("__c", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var baseName = value[..^3];
        var parts = baseName
            .Split(new[] { '_', ' ', '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]);

        return string.Join("_", parts) + "__c";
    }

    private static string ToPascalCustomName(string value)
    {
        var parts = Regex.Split(value.Trim('_'), @"[_\s]+")
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => char.ToUpperInvariant(part[0]) + (part.Length > 1 ? part[1..] : string.Empty));

        return string.Join("_", parts);
    }

    private static string NormalizeFieldType(string? fieldType)
    {
        if (string.IsNullOrWhiteSpace(fieldType))
        {
            return "Text";
        }

        return fieldType.Trim().ToLowerInvariant() switch
        {
            "checkbox" or "boolean" => "Checkbox",
            "text" or "string" => "Text",
            "number" => "Number",
            "date" => "Date",
            "datetime" or "date/time" => "DateTime",
            "longtextarea" or "long text area" => "LongTextArea",
            "picklist" => "Picklist",
            _ => fieldType.Trim()
        };
    }

    private static string BuildLabel(string fieldApiName)
    {
        var baseName = fieldApiName.EndsWith("__c", StringComparison.OrdinalIgnoreCase)
            ? fieldApiName[..^3]
            : fieldApiName;
        return string.Join(" ", baseName.Split('_', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string NormalizeToken(string value)
    {
        return Regex.Replace(value, @"[^a-zA-Z0-9]", string.Empty).ToLowerInvariant();
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}

public sealed record MetadataPlan
{
    public List<MetadataFieldPlan> Fields { get; init; } = new();
    public List<MetadataProfileFlsPlan> ProfileFls { get; init; } = new();
}

public sealed record MetadataFieldPlan
{
    public string ObjectApiName { get; init; } = string.Empty;
    public string FieldApiName { get; init; } = string.Empty;
    public string Type { get; init; } = "Text";
    public string Label { get; init; } = string.Empty;
    public string? InlineHelpText { get; init; }
    public string? Description { get; init; }
    public int? Length { get; init; }
    public string? DefaultValue { get; init; }
    public bool? Required { get; init; }
}

public sealed record MetadataProfileFlsPlan
{
    public string ObjectApiName { get; init; } = string.Empty;
    public string FieldApiName { get; init; } = string.Empty;
    public List<MetadataProfileAccessPlan> Profiles { get; init; } = new();
    public bool ApplyReadOnlyToRemainingProfiles { get; init; }
}

public sealed record MetadataProfileAccessPlan
{
    public string Name { get; init; } = string.Empty;
    public bool Readable { get; init; } = true;
    public bool Editable { get; init; }
}
