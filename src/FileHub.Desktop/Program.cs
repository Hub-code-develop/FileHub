using Avalonia;

namespace FileHub.Desktop;

internal static class Program
{
    /// <summary>
    /// Folder passed on the command line or through a filehub:// link, consumed by the first tab.
    /// Kept as static state because Avalonia's lifetime gives no channel for startup arguments.
    /// </summary>
    public static string? StartupPath { get; private set; }

    /// <summary>True when the launch came from the OS (link) rather than a shell command.</summary>
    public static bool IsDeepLink { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        (StartupPath, IsDeepLink) = ParseStartupTarget(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect();

    /// <summary>
    /// Accepts "filehub &lt;path&gt;", "filehub filehub://open?path=&lt;url-encoded&gt;" and a bare
    /// "filehub://&lt;path&gt;". Unknown flags are ignored so the app still starts from a shortcut.
    /// </summary>
    private static (string? Path, bool DeepLink) ParseStartupTarget(string[] args)
    {
        foreach (var raw in args)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith('-') || raw.StartsWith("--", StringComparison.Ordinal))
                continue;

            if (raw.StartsWith("filehub://", StringComparison.OrdinalIgnoreCase))
            {
                return (ParseDeepLink(raw), true);
            }

            try
            {
                var full = Path.GetFullPath(raw);
                if (Directory.Exists(full) || File.Exists(full)) return (full, false);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Not a usable path; keep looking.
            }
        }
        return (null, false);
    }

    private static string? ParseDeepLink(string link)
    {
        try
        {
            var uri = new Uri(link);
            // filehub://open?path=<encoded>  or  filehub:///<encoded path>
            if (uri.Host.Equals("open", StringComparison.OrdinalIgnoreCase))
            {
                var query = uri.Query.TrimStart('?');
                foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = pair.Split('=', 2);
                    if (parts.Length == 2 && parts[0].Equals("path", StringComparison.OrdinalIgnoreCase))
                        return Uri.UnescapeDataString(parts[1]);
                }
                return null;
            }

            var combined = uri.Host + uri.AbsolutePath;
            return combined.Length > 0 ? Uri.UnescapeDataString(combined) : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }
}
