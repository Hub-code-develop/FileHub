using System.Diagnostics;

namespace Cobalt.Files.Services;

/// <summary>
/// 跨平台「移到回收站」。Windows 走 Shell 回收站 API，macOS 直接移到 ~/.Trash，
/// Linux 用 gio trash（失败则退回 XDG 回收站目录）。任何失败都会抛异常由 UI 提示，
/// 绝不做静默永久删除。
/// </summary>
public static class RecycleBinService
{
    public static void MoveToTrash(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new FileNotFoundException("项目已不存在", path);

        if (OperatingSystem.IsWindows()) MoveToTrashWindows(path);
        else if (OperatingSystem.IsMacOS()) MoveToTrashMac(path);
        else MoveToTrashLinux(path);
    }

    private static void MoveToTrashWindows(string path)
    {
        var ui = Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs;
        var recycle = Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin;
        if (Directory.Exists(path))
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, ui, recycle);
        else
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, ui, recycle);
    }

    private static void MoveToTrashMac(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var trash = Path.Combine(home, ".Trash");
        Directory.CreateDirectory(trash);
        var target = UniquePath(Path.Combine(trash, Path.GetFileName(path)));
        if (Directory.Exists(path)) Directory.Move(path, target);
        else File.Move(path, target);
    }

    private static void MoveToTrashLinux(string path)
    {
        if (TryRun("gio", ["trash", path])) return;

        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                       ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        var files = Path.Combine(dataHome, "Trash", "files");
        var info = Path.Combine(dataHome, "Trash", "info");
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(info);
        var name = Path.GetFileName(path);
        var target = UniquePath(Path.Combine(files, name));
        if (Directory.Exists(path)) Directory.Move(path, target);
        else File.Move(path, target);

        var trashInfo = Path.Combine(info, Path.GetFileName(target) + ".trashinfo");
        File.WriteAllText(trashInfo,
            $"[Trash Info]\nPath={path}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n");
    }

    private static bool TryRun(string exe, IEnumerable<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(10_000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{name} ({Guid.NewGuid():N}){ext}");
    }
}
