using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace eZBERP_AI_IDE.Services;

public sealed class BitbucketPullRequestService
{
    private readonly GitService _gitService;

    public BitbucketPullRequestService(GitService gitService)
    {
        _gitService = gitService;
    }

    public async Task<string> CreatePullRequestAsync(
        string repoPath,
        string sourceBranch,
        string targetBranch,
        string title,
        string description)
    {
        var workspace = GetSetting("BITBUCKET_WORKSPACE");
        var repoSlug = GetSetting("BITBUCKET_REPO_SLUG");

        if (string.IsNullOrWhiteSpace(workspace) || string.IsNullOrWhiteSpace(repoSlug))
        {
            (workspace, repoSlug) = await TryResolveRepoFromOriginAsync(repoPath);
        }

        var username = GetSetting("BITBUCKET_USERNAME");
        var appPassword = GetSetting("BITBUCKET_APP_PASSWORD");

        if (string.IsNullOrWhiteSpace(workspace)
            || string.IsNullOrWhiteSpace(repoSlug)
            || string.IsNullOrWhiteSpace(username)
            || string.IsNullOrWhiteSpace(appPassword))
        {
            throw new InvalidOperationException(
                "Bitbucket PR creation needs BITBUCKET_USERNAME and BITBUCKET_APP_PASSWORD. " +
                "Set BITBUCKET_WORKSPACE and BITBUCKET_REPO_SLUG too if the origin URL cannot be parsed.");
        }

        using var httpClient = new HttpClient();
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + appPassword));
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);

        var payload = new
        {
            title,
            description,
            source = new { branch = new { name = sourceBranch } },
            destination = new { branch = new { name = targetBranch } },
            close_source_branch = false
        };

        var json = JsonSerializer.Serialize(payload);
        var url = $"https://api.bitbucket.org/2.0/repositories/{workspace}/{repoSlug}/pullrequests";
        using var response = await httpClient.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Bitbucket PR creation failed: " + body);
        }

        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("links", out var links)
            && links.TryGetProperty("html", out var html)
            && html.TryGetProperty("href", out var href))
        {
            return href.GetString() ?? "Pull request created.";
        }

        return "Pull request created.";
    }

    private async Task<(string Workspace, string RepoSlug)> TryResolveRepoFromOriginAsync(string repoPath)
    {
        var origin = await _gitService.GetOriginUrlAsync(repoPath);
        if (string.IsNullOrWhiteSpace(origin))
        {
            return (string.Empty, string.Empty);
        }

        origin = origin.Trim();
        var marker = "bitbucket.org/";
        var markerIndex = origin.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return (string.Empty, string.Empty);
        }

        var remainder = origin[(markerIndex + marker.Length)..]
            .Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(":", "/", StringComparison.Ordinal);
        var parts = remainder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? (parts[0], parts[1]) : (string.Empty, string.Empty);
    }

    private static string GetSetting(string name)
    {
        return Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process)
            ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine)
            ?? string.Empty;
    }
}
