namespace FileHub.Code.Models;

/// <summary>
/// The parts of a VS Code extension manifest that FileHub can actually consume. FileHub does not
/// run extension JavaScript; it reads contribution metadata (languages, themes, snippets) so the
/// Monaco editor can offer the same language and colour definitions the user already has
/// installed in VS Code.
/// </summary>
public sealed class ExtensionInfo
{
    public required string Id { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public string Folder { get; init; } = string.Empty;
    public string SourceRoot { get; init; } = string.Empty;
    public bool Enabled { get; set; } = true;

    public IReadOnlyList<ExtensionLanguage> Languages { get; init; } = [];
    public IReadOnlyList<ExtensionGrammar> Grammars { get; init; } = [];
    public IReadOnlyList<ExtensionTheme> Themes { get; init; } = [];
    public IReadOnlyList<ExtensionSnippet> Snippets { get; init; } = [];
    public IReadOnlyList<string> Commands { get; init; } = [];

    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;
    public string Subtitle => string.IsNullOrWhiteSpace(Publisher) ? Id : $"{Publisher}.{Id.Split('.').LastOrDefault()}";
}

public sealed record ExtensionLanguage(
    string Id,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> Aliases);

public sealed record ExtensionGrammar(string Language, string ScopeName, string Path);

public sealed record ExtensionTheme(string Id, string Label, string Path, string UiTheme);

public sealed record ExtensionSnippet(string Language, string Path);
