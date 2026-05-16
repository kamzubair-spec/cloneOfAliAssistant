namespace eZBERP_AI_IDE.Models;

public sealed class GitCommandResult
{
    public bool IsSuccess { get; init; }
    public int ExitCode { get; init; }
    public string Output { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;

    public string CombinedOutput => string.Join(
        Environment.NewLine,
        new[] { Output, Error }.Where(value => !string.IsNullOrWhiteSpace(value)));
}
