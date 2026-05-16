namespace eZBERP_AI_IDE.Models;

public sealed class GitChangedFile
{
    public string Status { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;

    public string Display => string.IsNullOrWhiteSpace(Status)
        ? Path
        : $"{Status} {Path}";
}
