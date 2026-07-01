namespace ClashSuki.Services;

public sealed record RuleProviderDocument(
    string Title,
    string Content,
    string SourcePath,
    string Format);
