using System.Diagnostics;
using eZBERP_AI_IDE.Models;

namespace eZBERP_AI_IDE.Services;

public sealed class GitService
{
    public async Task<IReadOnlyList<GitChangedFile>> GetChangedFilesAsync(string repoPath)
    {
        var result = await RunGitAsync(repoPath, "status --short");
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(result.CombinedOutput);
        }

        return result.Output
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseStatusLine)
            .Where(file => !string.IsNullOrWhiteSpace(file.Path))
            .ToList();
    }

    public Task<GitCommandResult> GetDiffAsync(string repoPath, string filePath)
    {
        return RunGitAsync(repoPath, $"diff -- \"{EscapeArgument(filePath)}\"");
    }

    public Task<GitCommandResult> GetStagedDiffAsync(string repoPath, string filePath)
    {
        return RunGitAsync(repoPath, $"diff --cached -- \"{EscapeArgument(filePath)}\"");
    }

    public Task<GitCommandResult> GetCurrentBranchAsync(string repoPath)
    {
        return RunGitAsync(repoPath, "branch --show-current");
    }

    public Task<GitCommandResult> CreateAndCheckoutBranchAsync(string repoPath, string branchName)
    {
        return RunGitAsync(repoPath, $"checkout -b \"{EscapeArgument(branchName)}\"");
    }

    public Task<GitCommandResult> CheckoutBranchAsync(string repoPath, string branchName)
    {
        return RunGitAsync(repoPath, $"checkout \"{EscapeArgument(branchName)}\"");
    }

    public Task<GitCommandResult> StageAllAsync(string repoPath)
    {
        return RunGitAsync(repoPath, "add -A");
    }

    public Task<GitCommandResult> CommitAsync(string repoPath, string message)
    {
        return RunGitAsync(repoPath, $"commit -m \"{EscapeArgument(message)}\"");
    }

    public Task<GitCommandResult> PushCurrentBranchAsync(string repoPath)
    {
        return RunGitAsync(repoPath, "push -u origin HEAD");
    }

    public async Task<string> GetOriginUrlAsync(string repoPath)
    {
        var result = await RunGitAsync(repoPath, "remote get-url origin");
        return result.IsSuccess ? result.Output.Trim() : string.Empty;
    }

    private static GitChangedFile ParseStatusLine(string line)
    {
        if (line.Length < 4)
        {
            return new GitChangedFile { Status = line.Trim(), Path = string.Empty };
        }

        var path = line[3..].Trim();
        if (path.Contains(" -> ", StringComparison.Ordinal))
        {
            path = path.Split(" -> ", StringSplitOptions.TrimEntries).Last();
        }

        return new GitChangedFile
        {
            Status = line[..2].Trim(),
            Path = path
        };
    }

    private static async Task<GitCommandResult> RunGitAsync(string repoPath, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start git.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new GitCommandResult
        {
            IsSuccess = process.ExitCode == 0,
            ExitCode = process.ExitCode,
            Output = await outputTask,
            Error = await errorTask,
            Command = "git " + arguments
        };
    }

    private static string EscapeArgument(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}
