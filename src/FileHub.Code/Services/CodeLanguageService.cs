namespace FileHub.Code.Services;

/// <summary>
/// Maps file extensions to Monaco language ids. Built-in ids cover the languages bundled with
/// monaco-editor 0.52.2; extension-contributed ids are layered on top by
/// <see cref="ExtensionDiscoveryService"/> so a VS Code language extension can extend the map.
/// </summary>
public static class CodeLanguageService
{
    // Every id here exists in Monaco's min/vs/editor/editor.main.js bundle. An unknown id would
    // render as plain text, so extension ids are only used after being registered in JavaScript.
    private static readonly Dictionary<string, string> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp", [".csx"] = "csharp", [".fs"] = "fsharp", [".fsx"] = "fsharp",
        [".fsi"] = "fsharp", [".vb"] = "vb", [".razor"] = "razor",
        [".js"] = "javascript", [".mjs"] = "javascript", [".cjs"] = "javascript",
        [".jsx"] = "javascript", [".es6"] = "javascript", [".ts"] = "typescript",
        [".tsx"] = "typescript", [".mts"] = "typescript", [".cts"] = "typescript",
        [".py"] = "python", [".pyw"] = "python", [".java"] = "java", [".kt"] = "kotlin",
        [".kts"] = "kotlin", [".go"] = "go", [".rs"] = "rust",
        [".c"] = "c", [".h"] = "cpp", [".cpp"] = "cpp", [".cc"] = "cpp",
        [".cxx"] = "cpp", [".hpp"] = "cpp", [".hh"] = "cpp", [".hxx"] = "cpp",
        [".m"] = "objective-c", [".mm"] = "objective-c", [".swift"] = "swift",
        [".php"] = "php", [".rb"] = "ruby", [".sh"] = "shell", [".bash"] = "shell",
        [".zsh"] = "shell", [".fish"] = "shell", [".ps1"] = "powershell",
        [".html"] = "html", [".htm"] = "html", [".xhtml"] = "html", [".vue"] = "html",
        [".svelte"] = "html", [".handlebars"] = "handlebars", [".hbs"] = "handlebars",
        [".css"] = "css", [".scss"] = "scss", [".less"] = "less",
        [".json"] = "json", [".jsonc"] = "json", [".xml"] = "xml", [".xsd"] = "xml",
        [".xsl"] = "xml", [".svg"] = "xml", [".yaml"] = "yaml", [".yml"] = "yaml",
        [".md"] = "markdown", [".markdown"] = "markdown", [".mdx"] = "mdx",
        [".sql"] = "sql", [".mysql"] = "mysql", [".pgsql"] = "pgsql", [".lua"] = "lua",
        [".dart"] = "dart", [".r"] = "r", [".pl"] = "perl", [".pm"] = "perl",
        [".toml"] = "ini", [".ini"] = "ini", [".cfg"] = "ini", [".conf"] = "ini",
        [".dockerfile"] = "dockerfile",
        [".bat"] = "bat", [".cmd"] = "bat", [".graphql"] = "graphql", [".gql"] = "graphql",
        [".proto"] = "protobuf", [".tf"] = "hcl", [".hcl"] = "hcl", [".scala"] = "scala",
        [".clj"] = "clojure", [".ex"] = "elixir", [".exs"] = "elixir",
        [".sol"] = "solidity", [".zig"] = "cpp",
        [".coffee"] = "coffee", [".pug"] = "pug", [".jade"] = "pug",
        [".twig"] = "twig", [".liquid"] = "liquid", [".st"] = "st",
        [".vbnet"] = "vb", [".pyi"] = "python", [".ipynb"] = "json"
    };

    /// <summary>Extensions that should open in the code editor on double-click.</summary>
    public static bool IsCodeFile(string path)
        => GetLanguageId(path, null) is not null;

    /// <summary>
    /// Returns a Monaco language id, or null when the file has no known code/text type.
    /// Extension-contributed mappings win over built-ins so users can override an association.
    /// </summary>
    public static string? GetLanguageId(string path, IReadOnlyDictionary<string, string>? extensionMap)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return null;
        if (extensionMap is not null && extensionMap.TryGetValue(ext, out var contributed)) return contributed;
        return BuiltIn.TryGetValue(ext, out var builtIn) ? builtIn : null;
    }

    /// <summary>All built-in language ids, used to avoid re-registering them from an extension.</summary>
    public static IReadOnlySet<string> BuiltInLanguageIds { get; } =
        BuiltIn.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
}
