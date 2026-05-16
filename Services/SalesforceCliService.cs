using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace eZBERP_AI_IDE.Services;

public sealed class SalesforceCliService
{
    private const string DefaultSfCliPath = @"C:\Program Files\sf\bin\sf.cmd";

    public async Task<List<OrgInfo>> GetOrgListAsync()
    {
        var result = await RunSfCommandAsync("org list --json", null);
        var orgs = new List<OrgInfo>();

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return orgs;
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            if (document.RootElement.TryGetProperty("result", out var resultNode))
            {
                AddOrgEntries(resultNode, "nonScratchOrgs", orgs, null);
                AddOrgEntries(resultNode, "scratchOrgs", orgs, true);
            }
        }
        catch { }

        return orgs;
    }

    public async Task<string?> GetLatestApiVersionAsync(string orgAlias)
    {
        var result = await RunSfCommandAsync($"org display --target-org {orgAlias} --json", null);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            if (document.RootElement.TryGetProperty("result", out var resultNode) &&
                resultNode.TryGetProperty("apiVersion", out var versionNode))
            {
                return versionNode.GetString();
            }
        }
        catch { }
        return null;
    }

    public async Task<ProcessResult> DeployToOrgAsync(string repoPath, string orgAlias, int waitMinutes = 10, string? apiVersionOverride = null)
    {
        var apiVersion = apiVersionOverride ?? await TryGetSourceApiVersionAsync(repoPath);
        var apiVersionArg = string.IsNullOrWhiteSpace(apiVersion) ? string.Empty : $" --api-version {apiVersion}";
        var command = $"project deploy start --source-dir force-app --target-org {orgAlias} --wait {waitMinutes}{apiVersionArg}";
        return await RunSfCommandAsync(command, repoPath, apiVersion);
    }

    public async Task<ProcessResult> DeployFilesToOrgAsync(string repoPath, string orgAlias, IEnumerable<string> relativeFilePaths, int waitMinutes = 10, string? apiVersionOverride = null)
    {
        return await DeployFilesToOrgAsync(repoPath, orgAlias, relativeFilePaths, waitMinutes, apiVersionOverride, false);
    }

    public async Task<ProcessResult> ValidateFilesToOrgAsync(string repoPath, string orgAlias, IEnumerable<string> relativeFilePaths, int waitMinutes = 10, string? apiVersionOverride = null)
    {
        return await DeployFilesToOrgAsync(repoPath, orgAlias, relativeFilePaths, waitMinutes, apiVersionOverride, true);
    }

    private async Task<ProcessResult> DeployFilesToOrgAsync(string repoPath, string orgAlias, IEnumerable<string> relativeFilePaths, int waitMinutes, string? apiVersionOverride, bool dryRun)
    {
        var apiVersion = apiVersionOverride ?? await TryGetSourceApiVersionAsync(repoPath);
        var apiVersionArg = string.IsNullOrWhiteSpace(apiVersion) ? string.Empty : $" --api-version {apiVersion}";
        var filesArg = string.Join(" ", relativeFilePaths.Select(p => $"--source-dir \"{p}\""));
        var dryRunArg = dryRun ? " --dry-run" : string.Empty;
        var command = $"project deploy start {filesArg} --target-org {orgAlias} --wait {waitMinutes}{apiVersionArg}{dryRunArg}";
        return await RunSfCommandAsync(command, repoPath, apiVersion);
    }

    public async Task<string> RunCommandAsync(string repoPath, string command)
    {
        var result = await RunSfCommandAsync(command, repoPath);
        return string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardError : result.StandardOutput;
    }

    private static async Task<string?> TryGetSourceApiVersionAsync(string repoPath)
    {
        try
        {
            var projectPath = Path.Combine(repoPath, "sfdx-project.json");
            if (!File.Exists(projectPath))
            {
                return null;
            }

            var content = await File.ReadAllTextAsync(projectPath);
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("sourceApiVersion", out var versionNode))
            {
                var versionStr = versionNode.GetString();
                if (double.TryParse(versionStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var version) && version < 63.0)
                {
                    // ViewAllFields requires at least 63.0
                    return "63.0";
                }
                return versionStr;
            }
        }
        catch
        {
            // Ignore errors reading sfdx-project.json
        }

        return null;
    }

    private static async Task<ProcessResult> RunSfCommandAsync(string arguments, string? workingDirectory, string? apiVersion = null)
    {
        var (fileName, resolvedArguments) = ResolveSfCommand(arguments);
        var result = await RunProcessAsync(fileName, resolvedArguments, workingDirectory, apiVersion);
        return result with { Command = $"sf {arguments}" };
    }

    private static (string FileName, string Arguments) ResolveSfCommand(string arguments)
    {
        if (File.Exists(DefaultSfCliPath))
        {
            return (DefaultSfCliPath, arguments);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var localSfPath = Path.Combine(localAppData, "sf", "bin", "sf.cmd");
        if (File.Exists(localSfPath))
        {
            return (localSfPath, arguments);
        }

        return ("cmd.exe", $"/c sf {arguments}");
    }

    private static async Task<ProcessResult> RunProcessAsync(string fileName, string arguments, string? workingDirectory, string? apiVersion = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? AppContext.BaseDirectory
                : workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrWhiteSpace(apiVersion))
        {
            startInfo.EnvironmentVariables["SF_ORG_API_VERSION"] = apiVersion;
            startInfo.EnvironmentVariables["SF_API_VERSION"] = apiVersion;
        }

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        var error = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                output.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                error.AppendLine(e.Data);
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, string.Empty, ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        return new ProcessResult(process.ExitCode, output.ToString().Trim(), error.ToString().Trim());
    }

    private static void AddOrgEntries(JsonElement resultNode, string propertyName, List<OrgInfo> orgs, bool? sandboxOverride)
    {
        if (!resultNode.TryGetProperty(propertyName, out var orgArray))
        {
            return;
        }

        foreach (var org in orgArray.EnumerateArray())
        {
            var isSandbox = sandboxOverride ?? (org.TryGetProperty("isSandbox", out var sandboxProperty) && sandboxProperty.GetBoolean());
            orgs.Add(new OrgInfo
            {
                Alias = org.TryGetProperty("alias", out var alias) ? alias.GetString() ?? string.Empty : string.Empty,
                Username = org.TryGetProperty("username", out var username) ? username.GetString() ?? string.Empty : string.Empty,
                InstanceUrl = org.TryGetProperty("instanceUrl", out var url) ? url.GetString() ?? string.Empty : string.Empty,
                IsSandbox = isSandbox
            });
        }
    }
}

public sealed class OrgInfo
{
    public string Alias { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string InstanceUrl { get; set; } = string.Empty;
    public bool IsSandbox { get; set; }

    public override string ToString()
    {
        var orgType = IsSandbox ? "Sandbox" : "Production";
        return $"{Alias} - {Username} ({orgType})";
    }
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string Command { get; init; } = string.Empty;
    public string CombinedOutput => string.IsNullOrWhiteSpace(StandardError)
        ? StandardOutput
        : $"{StandardError}\n{StandardOutput}".Trim();
}
