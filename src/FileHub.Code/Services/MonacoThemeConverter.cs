using System.Text.Json;
using System.Text.Json.Nodes;

namespace FileHub.Code.Services;

/// <summary>
/// Converts a VS Code colour theme to monaco-editor's defineTheme payload. VS Code themes carry
/// workbench colours plus TextMate token rules; Monaco understands the editor subset and its rule
/// shape is the same, so the conversion is mostly key filtering and hex normalisation.
/// </summary>
public static class MonacoThemeConverter
{
    // Monaco's theme service accepts a restricted set of colour keys. Passing workbench-only
    // keys through makes defineTheme throw, so keep an explicit allow-list.
    private static readonly HashSet<string> EditorColors = new(StringComparer.OrdinalIgnoreCase)
    {
        "editor.background", "editor.foreground", "editor.lineHighlightBackground",
        "editor.lineHighlightBorder", "editor.selectionBackground", "editor.inactiveSelectionBackground",
        "editor.selectionHighlightBackground", "editor.wordHighlightBackground",
        "editor.wordHighlightStrongBackground", "editor.findMatchBackground",
        "editor.findMatchHighlightBackground", "editorCursor.foreground", "editorWhitespace.foreground",
        "editorIndentGuide.background1", "editorIndentGuide.activeBackground1",
        "editorLineNumber.foreground", "editorLineNumber.activeForeground",
        "editorGutter.background", "editorGutter.modifiedBackground", "editorGutter.addedBackground",
        "editorGutter.deletedBackground", "editorBracketMatch.background", "editorBracketMatch.border",
        "editorBracketHighlight.foreground1", "editorBracketHighlight.foreground2",
        "editorBracketHighlight.foreground3", "editorBracketHighlight.foreground4",
        "editorBracketHighlight.foreground5", "editorBracketHighlight.foreground6",
        "editorWidget.background", "editorWidget.foreground", "editorWidget.border",
        "editorSuggestWidget.background", "editorSuggestWidget.foreground",
        "editorSuggestWidget.selectedBackground", "editorSuggestWidget.border",
        "editorHoverWidget.background", "editorHoverWidget.foreground", "editorHoverWidget.border",
        "editorOverviewRuler.border", "minimap.background", "minimapSlider.background",
        "scrollbarSlider.background", "scrollbarSlider.hoverBackground", "scrollbarSlider.activeBackground",
        "editorError.foreground", "editorWarning.foreground", "editorInfo.foreground",
        "editorLink.activeForeground"
    };

    public static string ConvertFile(string path)
        => Convert(File.ReadAllText(path));

    public static string Convert(string vsCodeThemeJson)
    {
        using var doc = JsonDocument.Parse(vsCodeThemeJson, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return "{}";

        var type = GetString(root, "type");
        var baseTheme = type switch
        {
            "dark" => "vs-dark",
            "hc" => "hc-black",
            "hc-light" => "hc-light",
            _ => "vs"
        };
        // A VS Code theme can inherit from a built-in; carry that through when it is one Monaco knows.
        var include = GetString(root, "include");
        if (include is "vs-dark" or "vs" or "hc-black" or "hc-light") baseTheme = include;

        var colors = new JsonObject();
        if (root.TryGetProperty("colors", out var sourceColors) && sourceColors.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in sourceColors.EnumerateObject())
            {
                if (!EditorColors.Contains(prop.Name)) continue;
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                var hex = NormalizeHex(prop.Value.GetString());
                if (hex is not null) colors[prop.Name] = hex;
            }
        }

        var rules = new JsonArray();
        if (root.TryGetProperty("tokenColors", out var tokenColors) && tokenColors.ValueKind == JsonValueKind.Array)
        {
            foreach (var token in tokenColors.EnumerateArray())
            {
                if (token.ValueKind != JsonValueKind.Object) continue;
                if (!token.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object) continue;

                var foreground = NormalizeHex(GetString(settings, "foreground"));
                var background = NormalizeHex(GetString(settings, "background"));
                var fontStyle = GetString(settings, "fontStyle");
                if (foreground is null && background is null && string.IsNullOrWhiteSpace(fontStyle)) continue;

                var scopes = new List<string>();
                if (token.TryGetProperty("scope", out var scope))
                {
                    if (scope.ValueKind == JsonValueKind.String) scopes.Add(scope.GetString()!);
                    else if (scope.ValueKind == JsonValueKind.Array)
                        scopes.AddRange(scope.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
                }
                // A tokenColors entry with no scope is the global default; Monaco uses "source".
                if (scopes.Count == 0) scopes.Add("source");

                foreach (var tokenScope in scopes)
                {
                    var rule = new JsonObject { ["token"] = tokenScope };
                    if (foreground is not null) rule["foreground"] = foreground;
                    if (background is not null) rule["background"] = background;
                    if (!string.IsNullOrWhiteSpace(fontStyle)) rule["fontStyle"] = fontStyle;
                    rules.Add(rule);
                }
            }
        }

        var result = new JsonObject
        {
            ["base"] = baseTheme,
            ["inherit"] = true,
            ["rules"] = rules,
            ["colors"] = colors
        };
        return result.ToJsonString();
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? NormalizeHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim().TrimStart('#');
        if (s.Length == 3)
            s = string.Concat(s.Select(ch => $"{ch}{ch}"));
        if (s.Length is not (6 or 8)) return null;
        return s.All(Uri.IsHexDigit) ? "#" + s.ToUpperInvariant() : null;
    }
}
