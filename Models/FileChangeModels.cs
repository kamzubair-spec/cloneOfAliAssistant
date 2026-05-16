namespace eZBERP_AI_IDE.Models;

public sealed record FileChangeProposal(
    string RelativePath,
    string ExistingContent,
    string ProposedContent,
    bool FileExists = true);

public sealed record FileChangeSet(
    string Title,
    IReadOnlyList<FileChangeProposal> Files,
    IReadOnlyList<string>? Messages = null);

public sealed record RequestedFileWrite(
    string RelativePath,
    string Content);
