using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using eZBERP_AI_IDE.Models;

namespace eZBERP_AI_IDE.Services;

public sealed class JiraService
{
    private readonly HttpClient _httpClient = new();
    private string? _acceptanceCriteriaFieldId;
    private string _lastSearchJql = string.Empty;
    private int _lastSearchResultCount;

    public string LastSearchJql => _lastSearchJql;
    public int LastSearchResultCount => _lastSearchResultCount;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(Email)
        && !string.IsNullOrWhiteSpace(ApiToken);

    private static string BaseUrl => GetSetting("JIRA_BASE_URL").TrimEnd('/');
    private static string Email => GetSetting("JIRA_EMAIL");
    private static string ApiToken => GetSetting("JIRA_API_TOKEN");

    private static string GetSetting(string name)
    {
        return FirstNonBlank(
            Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process),
            Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine));
    }

    private static string FirstNonBlank(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }
    public async Task<IReadOnlyList<JiraWorkItem>> SearchStoriesAsync(JiraStoryFilter filter)
    {
        EnsureConfigured();
        ConfigureAuthHeader();

        _lastSearchJql = BuildJql(filter);
        _lastSearchResultCount = 0;

        var payload = new
        {
            jql = _lastSearchJql,
            maxResults = Math.Clamp(filter.MaxResults, 1, 200),
            fields = new[]
            {
                "summary",
                "status",
                "assignee",
                "fixVersions",
                "customfield_10020",
                "customfield_10016"
            }
        };

        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync($"{BaseUrl}/rest/api/3/search/jql", content);
        var responseJson = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Jira search failed ({response.StatusCode}) using /rest/api/3/search/jql: {responseJson}");
        }

        using var document = JsonDocument.Parse(responseJson);
        var issues = document.RootElement.GetProperty("issues");
        var results = new List<JiraWorkItem>();

        foreach (var issue in issues.EnumerateArray())
        {
            var key = issue.GetProperty("key").GetString() ?? string.Empty;
            var fields = issue.GetProperty("fields");
            results.Add(new JiraWorkItem
            {
                Key = key,
                Summary = GetString(fields, "summary"),
                Status = GetNestedString(fields, "status", "name"),
                Assignee = GetNestedString(fields, "assignee", "displayName"),
                FixVersions = GetFixVersions(fields),
                Sprint = GetSprint(fields),
                StoryPoints = GetStoryPoints(fields)
            });
        }

        _lastSearchResultCount = results.Count;
        return results;
    }

    public async Task<string> GetStoryTextAsync(string issueKey)
    {
        EnsureConfigured();
        ConfigureAuthHeader();

        var acceptanceCriteriaFieldId = await GetAcceptanceCriteriaFieldIdAsync();
        using var response = await _httpClient.GetAsync($"{BaseUrl}/rest/api/3/issue/{Uri.EscapeDataString(issueKey)}?fields={BuildIssueFieldsParameter(acceptanceCriteriaFieldId)}");
        var responseJson = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Jira issue load failed ({response.StatusCode}): {responseJson}");
        }

        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        var fields = root.GetProperty("fields");
        var key = root.GetProperty("key").GetString() ?? issueKey;
        var summary = GetString(fields, "summary");
        var status = GetNestedString(fields, "status", "name");
        var assignee = GetNestedString(fields, "assignee", "displayName");
        var sprint = GetSprint(fields);
        var fixVersions = GetFixVersions(fields);
        var description = fields.TryGetProperty("description", out var descriptionElement)
            ? ExtractPlainText(descriptionElement)
            : string.Empty;
        var acceptanceCriteria = ExtractFieldPlainText(fields, acceptanceCriteriaFieldId);

        var builder = new StringBuilder();
        builder.AppendLine($"{key}: {summary}");
        builder.AppendLine();
        builder.AppendLine($"Status: {status}");
        builder.AppendLine($"Assignee: {assignee}");
        builder.AppendLine($"Sprint: {sprint}");
        builder.AppendLine($"Fix Versions: {fixVersions}");
        builder.AppendLine();
        builder.AppendLine(description);
        if (!string.IsNullOrWhiteSpace(acceptanceCriteria))
        {
            builder.AppendLine();
            builder.AppendLine("Acceptance Criteria");
            builder.AppendLine(acceptanceCriteria);
        }

        return builder.ToString().Trim();
    }

    public async Task<string> GetStoryAnalysisTextAsync(string issueKey)
    {
        return (await GetStoryAnalysisContentAsync(issueKey)).PlainText;
    }

    public async Task<JiraStoryAnalysisContent> GetStoryAnalysisContentAsync(string issueKey)
    {
        EnsureConfigured();
        ConfigureAuthHeader();

        var acceptanceCriteriaFieldId = await GetAcceptanceCriteriaFieldIdAsync();
        using var response = await _httpClient.GetAsync($"{BaseUrl}/rest/api/3/issue/{Uri.EscapeDataString(issueKey)}?expand=renderedFields&fields={BuildIssueFieldsParameter(acceptanceCriteriaFieldId)}");
        var responseJson = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Jira issue analysis load failed ({response.StatusCode}): {responseJson}");
        }

        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        var fields = root.GetProperty("fields");
        var key = root.GetProperty("key").GetString() ?? issueKey;
        var summary = GetString(fields, "summary");
        var status = GetNestedString(fields, "status", "name");
        var assignee = GetNestedString(fields, "assignee", "displayName");
        var sprint = GetSprint(fields);
        var fixVersions = GetFixVersions(fields);

        var renderedDescription = GetRenderedDescription(root, fields);
        var acceptanceCriteriaHtml = BuildAcceptanceCriteriaHtml(root, fields, acceptanceCriteriaFieldId);
        var attachmentPreviews = await BuildAttachmentPreviewsAsync(fields);
        renderedDescription = RemoveStruckThroughHtml(renderedDescription);
        acceptanceCriteriaHtml = RemoveStruckThroughHtml(acceptanceCriteriaHtml);
        renderedDescription = InlineAttachmentImages(renderedDescription, attachmentPreviews);
        var descriptionBlocks = HtmlToAnalysisBlocks(renderedDescription, attachmentPreviews);

        var builder = new StringBuilder();
        var blocks = new List<JiraStoryAnalysisBlock>();
        builder.AppendLine($"{key}: {summary}");
        builder.AppendLine();
        builder.AppendLine($"Status: {status}");
        builder.AppendLine($"Assignee: {assignee}");
        builder.AppendLine($"Sprint: {sprint}");
        builder.AppendLine($"Fix Versions: {fixVersions}");
        builder.AppendLine();
        builder.AppendLine("Description");
        blocks.Add(new JiraStoryAnalysisBlock
        {
            Kind = "text",
            Text = builder.ToString()
        });

        foreach (var block in descriptionBlocks)
        {
            blocks.Add(block);
            if (block.Kind.Equals("image", StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine($"[Inline image at this point: {block.FileName}]");
            }
            else
            {
                builder.AppendLine(block.Text);
            }
        }

        var acceptanceCriteriaText = HtmlToAnalysisText(acceptanceCriteriaHtml);
        if (!string.IsNullOrWhiteSpace(acceptanceCriteriaText))
        {
            builder.AppendLine();
            builder.AppendLine(acceptanceCriteriaText);
            blocks.Add(new JiraStoryAnalysisBlock
            {
                Kind = "text",
                Text = acceptanceCriteriaText
            });
        }

        var remainingAttachments = attachmentPreviews
            .Where(item => !item.WasRenderedInline)
            .Select(item => item.IsImage ? $"[Image attachment not placed inline: {item.FileName}]" : $"[Attachment: {item.FileName}]")
            .ToList();
        if (remainingAttachments.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Attachments");
            foreach (var attachment in remainingAttachments)
            {
                builder.AppendLine(attachment);
            }

            blocks.Add(new JiraStoryAnalysisBlock
            {
                Kind = "text",
                Text = "Attachments" + Environment.NewLine + string.Join(Environment.NewLine, remainingAttachments)
            });
        }

        var plainText = NormalizeAnalysisWhitespace(builder.ToString());
        foreach (var textBlock in blocks.Where(block => block.Kind.Equals("text", StringComparison.OrdinalIgnoreCase)))
        {
            textBlock.Text = NormalizeAnalysisWhitespace(textBlock.Text);
        }

        return new JiraStoryAnalysisContent
        {
            PlainText = plainText,
            Blocks = blocks
                .Where(block => block.Kind.Equals("image", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(block.Text))
                .ToList()
        };
    }

    public async Task<string> GetStoryHtmlAsync(string issueKey)
    {
        EnsureConfigured();
        ConfigureAuthHeader();

        var acceptanceCriteriaFieldId = await GetAcceptanceCriteriaFieldIdAsync();
        using var response = await _httpClient.GetAsync($"{BaseUrl}/rest/api/3/issue/{Uri.EscapeDataString(issueKey)}?expand=renderedFields&fields={BuildIssueFieldsParameter(acceptanceCriteriaFieldId)}");
        var responseJson = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Jira issue HTML load failed ({response.StatusCode}): {responseJson}");
        }

        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        var fields = root.GetProperty("fields");
        var key = root.GetProperty("key").GetString() ?? issueKey;
        var summary = GetString(fields, "summary");
        var status = GetNestedString(fields, "status", "name");
        var assignee = GetNestedString(fields, "assignee", "displayName");
        var sprint = GetSprint(fields);
        var fixVersions = GetFixVersions(fields);

        var renderedDescription = GetRenderedDescription(root, fields);

        var acceptanceCriteriaHtml = BuildAcceptanceCriteriaHtml(root, fields, acceptanceCriteriaFieldId);

        var attachmentPreviews = await BuildAttachmentPreviewsAsync(fields);
        renderedDescription = InlineAttachmentImages(renderedDescription, attachmentPreviews);
        var attachmentsHtml = BuildAttachmentsHtml(attachmentPreviews);

        return $$"""
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<style>
body { font-family: Segoe UI, Arial, sans-serif; margin: 0; color: #1f2933; background: #f5f7fb; }
.page { padding: 22px; }
.card { background: #fff; border: 1px solid #d9e2ec; border-radius: 10px; padding: 18px 20px; box-shadow: 0 8px 20px rgba(15,23,42,.08); }
h1 { font-size: 20px; margin: 0 0 8px; color: #102a43; }
.meta { display: flex; gap: 8px; flex-wrap: wrap; margin: 12px 0 18px; }
.pill { background: #e6f0ff; color: #174ea6; border-radius: 999px; padding: 5px 10px; font-size: 12px; font-weight: 600; }
.section-title { margin-top: 20px; font-size: 14px; text-transform: uppercase; letter-spacing: .06em; color: #52606d; }
.description { line-height: 1.5; font-size: 14px; }
.inline-attachment { margin: 14px 0 20px; border: 1px solid #d9e2ec; border-radius: 8px; padding: 10px; background: #fbfdff; }
.inline-attachment-image { max-width: 100%; max-height: 520px; border-radius: 6px; display: block; object-fit: contain; }
.caption { margin-top: 7px; color: #52606d; font-size: 12px; }
.attachments { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 14px; margin-top: 10px; }
.attachment { border: 1px solid #d9e2ec; border-radius: 8px; padding: 10px; background: #fbfdff; overflow: hidden; }
.attachment img { max-width: 100%; max-height: 360px; border-radius: 6px; display: block; margin-bottom: 8px; object-fit: contain; }
a { color: #0967d2; }
pre { white-space: pre-wrap; font-family: Consolas, monospace; }
</style>
</head>
<body>
<div class="page">
  <div class="card">
    <h1>{{WebUtility.HtmlEncode(key)}} - {{WebUtility.HtmlEncode(summary)}}</h1>
    <div class="meta">
      <span class="pill">Status: {{WebUtility.HtmlEncode(status)}}</span>
      <span class="pill">Assignee: {{WebUtility.HtmlEncode(assignee)}}</span>
      <span class="pill">Sprint: {{WebUtility.HtmlEncode(sprint)}}</span>
      <span class="pill">Fix: {{WebUtility.HtmlEncode(fixVersions)}}</span>
    </div>
    <div class="section-title">Description</div>
    <div class="description">{{renderedDescription}}</div>
    {{acceptanceCriteriaHtml}}
    {{attachmentsHtml}}
  </div>
</div>
</body>
</html>
""";
    }

    private async Task<string> GetAcceptanceCriteriaFieldIdAsync()
    {
        if (!string.IsNullOrWhiteSpace(_acceptanceCriteriaFieldId))
        {
            return _acceptanceCriteriaFieldId;
        }

        var configuredField = GetSetting("JIRA_ACCEPTANCE_CRITERIA_FIELD");
        if (!string.IsNullOrWhiteSpace(configuredField))
        {
            _acceptanceCriteriaFieldId = configuredField;
            return _acceptanceCriteriaFieldId;
        }

        _acceptanceCriteriaFieldId = await FindAcceptanceCriteriaFieldIdFromSearchAsync();
        if (!string.IsNullOrWhiteSpace(_acceptanceCriteriaFieldId))
        {
            return _acceptanceCriteriaFieldId;
        }

        _acceptanceCriteriaFieldId = await FindAcceptanceCriteriaFieldIdFromAllFieldsAsync();
        return _acceptanceCriteriaFieldId;
    }

    private static string GetRenderedDescription(JsonElement root, JsonElement fields)
    {
        var renderedDescription = string.Empty;
        if (root.TryGetProperty("renderedFields", out var renderedFields)
            && renderedFields.TryGetProperty("description", out var renderedDescriptionElement)
            && renderedDescriptionElement.ValueKind != JsonValueKind.Null)
        {
            renderedDescription = renderedDescriptionElement.GetString() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(renderedDescription)
            && fields.TryGetProperty("description", out var descriptionElement))
        {
            renderedDescription = $"<pre>{WebUtility.HtmlEncode(ExtractPlainText(descriptionElement))}</pre>";
        }

        return renderedDescription;
    }

    private async Task<string> FindAcceptanceCriteriaFieldIdFromSearchAsync()
    {
        using var response = await _httpClient.GetAsync($"{BaseUrl}/rest/api/3/field/search?query=Acceptance%20Criteria");
        if (!response.IsSuccessStatusCode)
        {
            return string.Empty;
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array
            ? FindAcceptanceCriteriaFieldId(values)
            : string.Empty;
    }

    private async Task<string> FindAcceptanceCriteriaFieldIdFromAllFieldsAsync()
    {
        using var response = await _httpClient.GetAsync($"{BaseUrl}/rest/api/3/field");
        if (!response.IsSuccessStatusCode)
        {
            return string.Empty;
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? FindAcceptanceCriteriaFieldId(document.RootElement)
            : string.Empty;
    }

    private static string FindAcceptanceCriteriaFieldId(JsonElement fields)
    {
        string? fuzzyMatch = null;

        foreach (var field in fields.EnumerateArray())
        {
            var name = field.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
            var id = field.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var normalizedName = NormalizeJiraFieldName(name);
            if (normalizedName == "acceptancecriteria")
            {
                return id;
            }

            if (normalizedName.Contains("acceptance", StringComparison.OrdinalIgnoreCase)
                && normalizedName.Contains("criteria", StringComparison.OrdinalIgnoreCase))
            {
                fuzzyMatch ??= id;
            }
        }

        return fuzzyMatch ?? string.Empty;
    }

    private static string NormalizeJiraFieldName(string? value)
    {
        return new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static string BuildIssueFieldsParameter(string acceptanceCriteriaFieldId)
    {
        const string baseFields = "summary,description,customfield_10020,status,assignee,fixVersions,attachment";
        return string.IsNullOrWhiteSpace(acceptanceCriteriaFieldId)
            ? baseFields
            : $"{baseFields},{Uri.EscapeDataString(acceptanceCriteriaFieldId)}";
    }

    private static string BuildAcceptanceCriteriaHtml(JsonElement root, JsonElement fields, string acceptanceCriteriaFieldId)
    {
        if (string.IsNullOrWhiteSpace(acceptanceCriteriaFieldId))
        {
            return string.Empty;
        }

        var renderedValue = string.Empty;
        if (root.TryGetProperty("renderedFields", out var renderedFields)
            && renderedFields.TryGetProperty(acceptanceCriteriaFieldId, out var renderedAcceptanceCriteria)
            && renderedAcceptanceCriteria.ValueKind != JsonValueKind.Null)
        {
            renderedValue = renderedAcceptanceCriteria.GetString() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(renderedValue))
        {
            var plainText = ExtractFieldPlainText(fields, acceptanceCriteriaFieldId);
            if (!string.IsNullOrWhiteSpace(plainText))
            {
                renderedValue = $"<pre>{WebUtility.HtmlEncode(plainText)}</pre>";
            }
        }

        return string.IsNullOrWhiteSpace(renderedValue)
            ? string.Empty
            : $"<div class=\"section-title\">Acceptance Criteria</div><div class=\"description\">{renderedValue}</div>";
    }

    private static string ExtractFieldPlainText(JsonElement fields, string fieldId)
    {
        if (string.IsNullOrWhiteSpace(fieldId)
            || !fields.TryGetProperty(fieldId, out var fieldValue)
            || fieldValue.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        return fieldValue.ValueKind == JsonValueKind.String
            ? fieldValue.GetString() ?? string.Empty
            : ExtractPlainText(fieldValue);
    }
    private async Task<List<JiraAttachmentPreview>> BuildAttachmentPreviewsAsync(JsonElement fields)
    {
        var previews = new List<JiraAttachmentPreview>();
        if (!fields.TryGetProperty("attachment", out var attachments) || attachments.ValueKind != JsonValueKind.Array || attachments.GetArrayLength() == 0)
        {
            return previews;
        }

        foreach (var attachment in attachments.EnumerateArray())
        {
            var fileName = attachment.TryGetProperty("filename", out var fileNameElement) ? fileNameElement.GetString() ?? "Attachment" : "Attachment";
            var mimeType = attachment.TryGetProperty("mimeType", out var mimeElement) ? mimeElement.GetString() ?? string.Empty : string.Empty;
            var contentUrl = attachment.TryGetProperty("content", out var contentElement) ? contentElement.GetString() ?? string.Empty : string.Empty;
            var thumbnailUrl = attachment.TryGetProperty("thumbnail", out var thumbnailElement) ? thumbnailElement.GetString() ?? string.Empty : string.Empty;
            var localImageUri = mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                ? await TryCacheAttachmentImageAsync(contentUrl, thumbnailUrl, fileName)
                : string.Empty;

            previews.Add(new JiraAttachmentPreview(fileName, mimeType, contentUrl, localImageUri));
        }

        return previews;
    }

    private static string InlineAttachmentImages(string renderedDescription, List<JiraAttachmentPreview> attachments)
    {
        if (string.IsNullOrWhiteSpace(renderedDescription) || attachments.Count == 0)
        {
            return renderedDescription;
        }

        foreach (var attachment in attachments.Where(item => item.IsImage && !string.IsNullOrWhiteSpace(item.LocalImageUri)))
        {
            var before = renderedDescription;
            var imageHtml = BuildInlineImageHtml(attachment);
            var encodedNamePattern = Regex.Escape(WebUtility.HtmlEncode(attachment.FileName));
            var plainNamePattern = Regex.Escape(attachment.FileName);
            var fileNamePattern = $"(?:{encodedNamePattern}|{plainNamePattern})";

            // Jira rendered HTML usually keeps the correct position, but its img/link points
            // to authenticated Jira content. Replace that broken inline marker with our cached image.
            renderedDescription = Regex.Replace(
                renderedDescription,
                $@"(?is)<img\b(?=[^>]*(?:alt|title)=[""']{fileNamePattern}[""'])[^>]*>",
                imageHtml);

            if (before == renderedDescription)
            {
                renderedDescription = Regex.Replace(
                    renderedDescription,
                    $@"(?is)<a\b[^>]*(?:attachment|secure|thumbnail|content)[^>]*>\s*(?:<img\b[^>]*>\s*)?{fileNamePattern}?\s*</a>",
                    imageHtml);
            }

            if (before == renderedDescription)
            {
                renderedDescription = Regex.Replace(
                    renderedDescription,
                    $@"(?is)<p>\s*(?:<span\b[^>]*>\s*)*(?:<img\b[^>]*>\s*)?{fileNamePattern}\s*(?:</span>\s*)*</p>",
                    imageHtml);
            }

            attachment.WasRenderedInline = before != renderedDescription;
        }

        return renderedDescription;
    }

    private static string BuildAttachmentsHtml(List<JiraAttachmentPreview> attachments)
    {
        var remaining = attachments.Where(item => !item.WasRenderedInline).ToList();
        if (remaining.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine("<div class=\"section-title\">Attachments</div>");
        builder.AppendLine("<div class=\"attachments\">");

        foreach (var attachment in remaining)
        {
            var safeName = WebUtility.HtmlEncode(attachment.FileName);
            builder.AppendLine("<div class=\"attachment\">");
            if (attachment.IsImage && !string.IsNullOrWhiteSpace(attachment.LocalImageUri))
            {
                builder.AppendLine(BuildImageTag(attachment, "attachment-image"));
            }

            builder.AppendLine(string.IsNullOrWhiteSpace(attachment.ContentUrl)
                ? $"<strong>{safeName}</strong>"
                : $"<a href=\"{WebUtility.HtmlEncode(attachment.ContentUrl)}\">{safeName}</a>");
            builder.AppendLine("</div>");
        }

        builder.AppendLine("</div>");
        return builder.ToString();
    }

    private static string BuildInlineImageHtml(JiraAttachmentPreview attachment)
    {
        return $"<div class=\"inline-attachment\">{BuildImageTag(attachment, "inline-attachment-image")}<div class=\"caption\">{WebUtility.HtmlEncode(attachment.FileName)}</div></div>";
    }

    private static string BuildImageTag(JiraAttachmentPreview attachment, string cssClass)
    {
        return $"<img class=\"{cssClass}\" src=\"{WebUtility.HtmlEncode(attachment.LocalImageUri)}\" alt=\"{WebUtility.HtmlEncode(attachment.FileName)}\">";
    }

    private static string HtmlToAnalysisText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var text = RemoveStruckThroughHtml(html);
        text = Regex.Replace(
            text,
            @"(?is)<div\b[^>]*class=[""'][^""']*inline-attachment[^""']*[""'][^>]*>.*?<img\b[^>]*alt=[""'](?<name>[^""']+)[""'][^>]*>.*?</div>",
            match => $"{Environment.NewLine}[Inline image at this point: {WebUtility.HtmlDecode(match.Groups["name"].Value)}]{Environment.NewLine}");
        text = Regex.Replace(
            text,
            @"(?is)<img\b[^>]*alt=[""'](?<name>[^""']+)[""'][^>]*>",
            match => $"{Environment.NewLine}[Inline image at this point: {WebUtility.HtmlDecode(match.Groups["name"].Value)}]{Environment.NewLine}");
        text = Regex.Replace(text, @"(?i)<\s*br\s*/?\s*>", Environment.NewLine);
        text = Regex.Replace(text, @"(?i)</\s*(p|div|li|tr|h[1-6]|pre)\s*>", Environment.NewLine);
        text = Regex.Replace(text, @"(?i)<\s*li\b[^>]*>", "- ");
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        return NormalizeAnalysisWhitespace(text);
    }

    private static List<JiraStoryAnalysisBlock> HtmlToAnalysisBlocks(string html, List<JiraAttachmentPreview> attachments)
    {
        var blocks = new List<JiraStoryAnalysisBlock>();
        if (string.IsNullOrWhiteSpace(html))
        {
            return blocks;
        }

        html = RemoveStruckThroughHtml(html);
        var imagePattern = @"(?is)<div\b[^>]*class=[""'][^""']*inline-attachment[^""']*[""'][^>]*>.*?<img\b(?<attrs>[^>]*)>.*?</div>|<img\b(?<attrs>[^>]*)>";
        var matches = Regex.Matches(html, imagePattern);
        var lastIndex = 0;

        foreach (Match match in matches)
        {
            if (match.Index > lastIndex)
            {
                AddTextBlock(blocks, html.Substring(lastIndex, match.Index - lastIndex));
            }

            var attrs = match.Groups["attrs"].Value;
            var fileName = GetHtmlAttribute(attrs, "alt");
            var src = GetHtmlAttribute(attrs, "src");
            var attachment = attachments.FirstOrDefault(item =>
                item.FileName.Equals(WebUtility.HtmlDecode(fileName), StringComparison.OrdinalIgnoreCase)
                || item.LocalImageUri.Equals(src, StringComparison.OrdinalIgnoreCase));

            var localPath = TryGetLocalPathFromUri(src);
            if (string.IsNullOrWhiteSpace(localPath) && attachment is not null)
            {
                localPath = TryGetLocalPathFromUri(attachment.LocalImageUri);
            }

            blocks.Add(new JiraStoryAnalysisBlock
            {
                Kind = "image",
                FileName = WebUtility.HtmlDecode(string.IsNullOrWhiteSpace(fileName) ? attachment?.FileName ?? "inline image" : fileName),
                MimeType = attachment?.MimeType ?? GuessMimeTypeFromPath(localPath),
                LocalPath = localPath,
                IsInlineImage = true
            });

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < html.Length)
        {
            AddTextBlock(blocks, html.Substring(lastIndex));
        }

        return blocks;
    }

    private static string RemoveStruckThroughHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var cleaned = Regex.Replace(html, @"(?is)<\s*(s|strike|del)\b[^>]*>.*?<\s*/\s*\1\s*>", " ");
        cleaned = Regex.Replace(
            cleaned,
            @"(?is)<(?<tag>span|p|div|td|th|li)\b(?=[^>]*(?:text-decoration\s*:\s*line-through|line-through|ak-renderer-mark-strike|fabric-editor-block-mark))[^>]*>.*?<\s*/\s*\k<tag>\s*>",
            " ");
        return cleaned;
    }

    private static void AddTextBlock(List<JiraStoryAnalysisBlock> blocks, string html)
    {
        var text = HtmlToAnalysisText(html);
        if (!string.IsNullOrWhiteSpace(text))
        {
            blocks.Add(new JiraStoryAnalysisBlock
            {
                Kind = "text",
                Text = text
            });
        }
    }

    private static string GetHtmlAttribute(string attributes, string name)
    {
        var match = Regex.Match(attributes, $@"(?is)\b{name}\s*=\s*[""'](?<value>[^""']*)[""']");
        return match.Success ? match.Groups["value"].Value : string.Empty;
    }

    private static string TryGetLocalPathFromUri(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return string.Empty;
        }

        try
        {
            var parsed = new Uri(WebUtility.HtmlDecode(uri));
            return parsed.IsFile ? parsed.LocalPath : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GuessMimeTypeFromPath(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "image/png"
        };
    }

    private static string NormalizeAnalysisWhitespace(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace("\r\n", "\n").Replace('\r', '\n');
        normalized = Regex.Replace(normalized, @"[ \t]+\n", "\n");
        normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n");
        normalized = Regex.Replace(normalized, @"[ \t]{2,}", " ");
        return normalized.Trim();
    }

    private async Task<string> TryCacheAttachmentImageAsync(string contentUrl, string thumbnailUrl, string fileName)
    {
        var localUri = await TryCacheAttachmentFileAsync(contentUrl, fileName);
        if (!string.IsNullOrWhiteSpace(localUri))
        {
            return localUri;
        }

        return await TryCacheAttachmentFileAsync(thumbnailUrl, fileName);
    }

    private async Task<string> TryCacheAttachmentFileAsync(string url, string fileName)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return string.Empty;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            const int maxCachedBytes = 10_000_000;
            if (bytes.Length == 0 || bytes.Length > maxCachedBytes)
            {
                return string.Empty;
            }

            var cacheFolder = Path.Combine(Path.GetTempPath(), "eZBERP_AI_IDE", "JiraAttachments");
            Directory.CreateDirectory(cacheFolder);

            var safeFileName = MakeSafeFileName(fileName);
            var cacheKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(url))).Substring(0, 16);
            var localPath = Path.Combine(cacheFolder, $"{cacheKey}_{safeFileName}");

            await File.WriteAllBytesAsync(localPath, bytes);
            return new Uri(localPath).AbsoluteUri;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string MakeSafeFileName(string fileName)
    {
        var cleaned = new string((string.IsNullOrWhiteSpace(fileName) ? "attachment" : fileName)
            .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)
            .ToArray());

        return string.IsNullOrWhiteSpace(cleaned) ? "attachment" : cleaned;
    }
    private sealed class JiraAttachmentPreview
    {
        public JiraAttachmentPreview(string fileName, string mimeType, string contentUrl, string localImageUri)
        {
            FileName = fileName;
            MimeType = mimeType;
            ContentUrl = contentUrl;
            LocalImageUri = localImageUri;
        }

        public string FileName { get; }
        public string MimeType { get; }
        public string ContentUrl { get; }
        public string LocalImageUri { get; }
        public bool WasRenderedInline { get; set; }
        public bool IsImage => MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    }
    private static string BuildJql(JiraStoryFilter filter)
    {
        var clauses = new List<string>();

        if (!string.IsNullOrWhiteSpace(filter.SpaceOrProject))
        {
            clauses.Add($"project = {FormatProjectValue(filter.SpaceOrProject)}");
        }

        if (!string.IsNullOrWhiteSpace(filter.IssueType))
        {
            clauses.Add($"issuetype = {Quote(filter.IssueType)}");
        }

        if (!string.IsNullOrWhiteSpace(filter.LeadConsultant))
        {
            clauses.Add(filter.LeadConsultant.Equals("Current User", StringComparison.OrdinalIgnoreCase)
                ? "\"Lead Consultant\" = currentUser()"
                : $"\"Lead Consultant\" = {Quote(filter.LeadConsultant)}");
        }

        if (!string.IsNullOrWhiteSpace(filter.Sprint))
        {
            clauses.Add($"Sprint = {Quote(filter.Sprint)}");
        }

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            clauses.Add($"status = {Quote(filter.Status)}");
        }

        if (!string.IsNullOrWhiteSpace(filter.SearchText))
        {
            clauses.Add($"text ~ {Quote(filter.SearchText)}");
        }

        return string.Join(" AND ", clauses) + " ORDER BY updated DESC";
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static string FormatProjectValue(string spaceOrProject)
    {
        var configuredProjectKey = GetSetting("JIRA_PROJECT_KEY");
        var projectValue = !string.IsNullOrWhiteSpace(configuredProjectKey)
            ? configuredProjectKey
            : spaceOrProject;

        return LooksLikeProjectKey(projectValue)
            ? projectValue
            : Quote(projectValue);
    }

    private static bool LooksLikeProjectKey(string value)
    {
        return Regex.IsMatch(value.Trim(), "^[A-Z][A-Z0-9_]+$");
    }

    private static string BuildConfigurationErrorMessage()
    {
        return "Jira is not configured. "
               + $"JIRA_BASE_URL: {DescribeSetting("JIRA_BASE_URL")}; "
               + $"JIRA_EMAIL: {DescribeSetting("JIRA_EMAIL")}; "
               + $"JIRA_API_TOKEN: {DescribeSetting("JIRA_API_TOKEN", maskValue: true)}.";
    }

    private static string DescribeSetting(string name, bool maskValue = false)
    {
        var process = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
        var user = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        var machine = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);

        if (!string.IsNullOrWhiteSpace(process))
        {
            return maskValue ? "set in Process" : $"set in Process ({process.Trim()})";
        }

        if (!string.IsNullOrWhiteSpace(user))
        {
            return maskValue ? "set in User" : $"set in User ({user.Trim()})";
        }

        if (!string.IsNullOrWhiteSpace(machine))
        {
            return maskValue ? "set in Machine" : $"set in Machine ({machine.Trim()})";
        }

        return "missing";
    }
    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(BuildConfigurationErrorMessage());
        }
    }

    private void ConfigureAuthHeader()
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Email}:{ApiToken}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static string GetString(JsonElement fields, string name)
    {
        return fields.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string GetNestedString(JsonElement fields, string fieldName, string nestedName)
    {
        return fields.TryGetProperty(fieldName, out var value)
               && value.ValueKind == JsonValueKind.Object
               && value.TryGetProperty(nestedName, out var nested)
            ? nested.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string GetFixVersions(JsonElement fields)
    {
        if (!fields.TryGetProperty("fixVersions", out var versions) || versions.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join(", ", versions.EnumerateArray()
            .Select(version => version.TryGetProperty("name", out var name) ? name.GetString() : string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name)));
    }

    private static string GetSprint(JsonElement fields)
    {
        if (!fields.TryGetProperty("customfield_10020", out var sprintValue) || sprintValue.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join(", ", sprintValue.EnumerateArray()
            .Select(sprint => sprint.TryGetProperty("name", out var name) ? name.GetString() : string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name)));
    }

    private static string GetStoryPoints(JsonElement fields)
    {
        if (!fields.TryGetProperty("customfield_10016", out var points) || points.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        return points.ValueKind == JsonValueKind.Number
            ? points.GetDecimal().ToString("0.##")
            : points.ToString();
    }

    private static string ExtractPlainText(JsonElement element)
    {
        var builder = new StringBuilder();
        AppendPlainText(element, builder);
        return builder.ToString().Trim();
    }

    private static void AppendPlainText(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (HasStrikeThroughMark(element))
                {
                    return;
                }

                if (element.TryGetProperty("text", out var text))
                {
                    builder.Append(text.GetString());
                }

                if (element.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var child in content.EnumerateArray())
                    {
                        AppendPlainText(child, builder);
                    }

                    builder.AppendLine();
                }
                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    AppendPlainText(child, builder);
                }
                break;
        }
    }

    private static bool HasStrikeThroughMark(JsonElement element)
    {
        if (!element.TryGetProperty("marks", out var marks) || marks.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var mark in marks.EnumerateArray())
        {
            if (mark.ValueKind == JsonValueKind.Object
                && mark.TryGetProperty("type", out var typeElement)
                && string.Equals(typeElement.GetString(), "strike", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}














