using System.Diagnostics;

namespace Cobalt.Files.Services;

public static class PlatformService
{
    /// <summary>在系统文件管理器里显示（macOS Finder / Windows 资源管理器 / Linux 文件管理器）。</summary>
    public static void Reveal(string path)
    {
        if (OperatingSystem.IsMacOS()) Start("open", ["-R", path]);
        else if (OperatingSystem.IsWindows()) Start("explorer.exe", [$"/select,\"{path}\""]);
        else Start("xdg-open", [Path.GetDirectoryName(path) ?? path]);
    }

    public static string PlatformName => OperatingSystem.IsWindows() ? "Windows"
        : OperatingSystem.IsMacOS() ? "macOS"
        : OperatingSystem.IsLinux() ? "Linux" : "Unknown";

    private static void Start(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }
}
