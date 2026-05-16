namespace eZBERP_AI_IDE.Models;

public sealed class JiraStoryAnalysisContent
{
    public string PlainText { get; set; } = string.Empty;
    public List<JiraStoryAnalysisBlock> Blocks { get; set; } = new();

    public bool HasInlineImages => Blocks.Any(block =>
        block.Kind.Equals("image", StringComparison.OrdinalIgnoreCase)
        && block.IsInlineImage);

    public bool HasReadableInlineImages => Blocks.Any(block =>
        block.Kind.Equals("image", StringComparison.OrdinalIgnoreCase)
        && block.IsInlineImage
        && !string.IsNullOrWhiteSpace(block.LocalPath)
        && File.Exists(block.LocalPath));
}

public sealed class JiraStoryAnalysisBlock
{
    public string Kind { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public bool IsInlineImage { get; set; }
}
