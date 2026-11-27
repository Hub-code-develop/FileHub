// Copyright (c) 2026 Hub-code-develop. Licensed under the MIT License.
//
// Batch rename engine for the "Batch rename" dialog. This project is logic-only: the caller
// injects the rename delegate, so the same engine drives the local disk and every virtual
// location (archive, SMB, SFTP, WebDAV, S3) without a second implementation.
//
// Pipeline for one entry:
//
//   pattern -> auto extension -> find/replace -> regex -> case -> prefix/suffix
//
// The pattern produces the complete file name ("{name}{ext}" by default). Prefix and suffix
// wrap the base name only, so the extension always survives. Regex and case folding also run on
// the base name, which keeps "IMG_0001.JPG" from becoming "IMG_0001.jpg" by accident.
//
// Pattern placeholders (case-insensitive, pipes apply left to right):
//
//   {name}        base name without extension (full name when IncludeExtensionInPattern)
//   {ext}         extension without the dot
//   {orig}        full original name
//   {n}           counter starting at Start, incremented by Step
//   {n:000}       counter padded with zeros to at least three digits
//   {date}        today, yyyyMMdd        {time}  now, HHmmss
//   {created}     creation date, yyyyMMdd (falls back to modified when unavailable)
//   {modified}    last write date, yyyyMMdd
//   {exif:date}   EXIF DateTimeOriginal, yyyyMMdd (falls back to modified)
//   {exif:model}  EXIF camera model, empty when absent
//
// Unknown placeholders are left verbatim so a typo shows up in the preview instead of silently
// deleting text.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FileHub.Files.Vfs;

namespace FileHub.Files.Services;

/// <summary>How the generated base name is cased before prefix/suffix are applied.</summary>
public enum RenameCaseMode
{
    Keep,
    Upper,
    Lower,
    Title
}

/// <summary>Input for <see cref="BatchRenameService"/>.</summary>
public sealed class BatchRenameOptions
{
    /// <summary>Name template. It produces the complete file name; the default keeps the original.</summary>
    public string Pattern { get; set; } = "{name}{ext}";

    /// <summary>First value of the <c>{n}</c> counter.</summary>
    public int Start { get; set; } = 1;

    /// <summary>Amount added to the counter for each following entry. May be negative.</summary>
    public int Step { get; set; } = 1;

    /// <summary>Text placed before the base name. Verbatim, never case-folded.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Text placed after the base name, before the extension. Verbatim.</summary>
    public string Suffix { get; set; } = string.Empty;

    /// <summary>Plain-text search inside the generated base name; null or empty disables it.</summary>
    public string? Find { get; set; }

    /// <summary>Replacement for <see cref="Find"/>. Null means "remove the match".</summary>
    public string? Replace { get; set; }

    /// <summary>Regular expression applied to the generated base name. Supports <c>$1</c> groups.</summary>
    public string? FindPattern { get; set; }

    /// <summary>Replacement for <see cref="FindPattern"/>; <c>$1</c> refers to capture group one.</summary>
    public string? Replacement { get; set; }

    /// <summary>Case folding applied after find/replace and regex, before prefix/suffix.</summary>
    public RenameCaseMode CaseMode { get; set; } = RenameCaseMode.Keep;

    /// <summary>
    /// When true, <c>{name}</c> expands to the full original name (extension included) and
    /// <c>{ext}</c> is empty. No extension is appended to the result, so the pattern owns the
    /// whole name. When false, <c>{name}</c> is the base name and a missing extension is restored.
    /// </summary>
    public bool IncludeExtensionInPattern { get; set; }
}

/// <summary>One row of the preview list.</summary>
public sealed class RenamePreviewItem
{
    /// <summary>Path as passed in by the caller (may be a virtual path).</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Current file name, extension included.</summary>
    public string SourceName { get; set; } = string.Empty;

    /// <summary>Proposed file name, extension included.</summary>
    public string TargetName { get; set; } = string.Empty;

    /// <summary>Proposed path: the source parent joined with <see cref="TargetName"/>.</summary>
    public string TargetPath { get; set; } = string.Empty;

    /// <summary>False when the generated name is empty, too long or contains illegal characters.</summary>
    public bool IsValid { get; set; }

    /// <summary>Validation, conflict or apply-failure message. Appended to, never overwritten.</summary>
    public string? Error { get; set; }

    /// <summary>True when the target name is duplicated in the batch or already exists.</summary>
    public bool Conflict { get; set; }
}

/// <summary>Result of <see cref="BatchRenameService.PreviewAsync"/>.</summary>
public sealed class RenamePlan
{
    /// <summary>Preview rows in the same order as the input paths.</summary>
    public IReadOnlyList<RenamePreviewItem> Items { get; set; } = [];

    /// <summary>True when at least one row is flagged as a conflict.</summary>
    public bool HasConflicts { get; set; }

    /// <summary>True when every row produced a legal name (conflicts are reported separately).</summary>
    public bool IsValid { get; set; }

    /// <summary>First validation error, or null when all names are legal.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Computes rename targets and applies a previewed plan. Pure logic: no UI, no direct rename.
/// </summary>
public static class BatchRenameService
{
    /// <summary>Maximum length of one file name, measured in UTF-8 bytes.</summary>
    public const int MaxNameBytes = 255;

    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private const int ExifChunkBytes = 64 * 1024;
    private const int ExifMaxBytes = 192 * 1024;

    /// <summary>
    /// Builds a plan for <paramref name="paths"/> without touching the file names. The preview
    /// reads metadata (dates, EXIF) and probes target existence; a rename only happens in
    /// <see cref="ApplyAsync"/>.
    /// </summary>
    public static async Task<RenamePlan> PreviewAsync(
        IReadOnlyList<string> paths, BatchRenameOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);

        var flags = InspectPattern(options.Pattern);
        var regex = BuildRegex(options.FindPattern, out var regexError);

        var items = new List<RenamePreviewItem>(paths.Count);
        for (var index = 0; index < paths.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            items.Add(await PreviewOneAsync(paths[index], index, options, flags, regex, regexError, ct)
                .ConfigureAwait(false));
        }

        await MarkConflictsAsync(items, ct).ConfigureAwait(false);

        var firstInvalid = items.FirstOrDefault(x => !x.IsValid);
        return new RenamePlan
        {
            Items = items,
            HasConflicts = items.Any(x => x.Conflict),
            IsValid = firstInvalid is null,
            Error = firstInvalid?.Error
        };
    }

    /// <summary>
    /// Applies a previewed plan one entry at a time. <paramref name="renameAsync"/> receives the
    /// current source path and the new file name, and returns the new path (the return value is
    /// not needed here). A single failure is recorded on the row and does not stop the batch.
    /// Rows with <see cref="RenamePreviewItem.IsValid"/> false or a conflict are skipped.
    /// </summary>
    /// <returns>The number of entries renamed successfully.</returns>
    public static async Task<int> ApplyAsync(
        RenamePlan plan, Func<string, string, Task<string>> renameAsync, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(renameAsync);

        var succeeded = 0;
        foreach (var item in plan.Items)
        {
            ct.ThrowIfCancellationRequested();

            if (!item.IsValid)
            {
                continue;
            }

            if (item.Conflict)
            {
                AppendError(item, "Skipped because the target already exists.");
                continue;
            }

            if (string.Equals(item.TargetName, item.SourceName, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                if (IsCaseOnlyRename(item))
                {
                    await RenameCaseOnlyAsync(item, renameAsync).ConfigureAwait(false);
                }
                else
                {
                    await renameAsync(item.SourcePath, item.TargetName).ConfigureAwait(false);
                }

                succeeded++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppendError(item, ex.Message);
            }
        }

        return succeeded;
    }

    // ---------------------------------------------------------------- preview helpers

    private readonly record struct PatternFlags(bool NeedsStat, bool NeedsExif);

    private static async Task<RenamePreviewItem> PreviewOneAsync(
        string path, int index, BatchRenameOptions options, PatternFlags flags,
        Regex? regex, string? regexError, CancellationToken ct)
    {
        var item = new RenamePreviewItem { SourcePath = path ?? string.Empty };
        if (string.IsNullOrWhiteSpace(path))
        {
            item.IsValid = false;
            item.Error = "The source path is empty.";
            return item;
        }

        VfsPath vfs;
        try
        {
            vfs = VfsPath.Parse(path);
        }
        catch (ArgumentException ex)
        {
            item.IsValid = false;
            item.Error = ex.Message;
            return item;
        }

        var sourceName = vfs.FileName;
        item.SourceName = sourceName;
        item.TargetName = sourceName;
        item.TargetPath = path;

        if (sourceName.Length == 0)
        {
            item.IsValid = false;
            item.Error = "The path does not end with a file name.";
            return item;
        }

        if (regexError is not null)
        {
            item.IsValid = false;
            item.Error = regexError;
            return item;
        }

        var extension = vfs.Extension; // ".jpg" or empty
        var baseName = extension.Length == 0 ? sourceName : sourceName[..^extension.Length];

        var modified = DateTimeOffset.Now;
        var created = modified;
        string? exifDate = null;
        string? exifModel = null;

        if (flags.NeedsStat || flags.NeedsExif)
        {
            var stat = await StatSafeAsync(path, ct).ConfigureAwait(false);
            if (stat.Exists)
            {
                modified = stat.Modified;
                created = stat.Modified;

                if (vfs.IsLocal)
                {
                    var inner = vfs.Inner;
                    created = await Task.Run(() => CreationTimeSafe(inner), ct).ConfigureAwait(false);
                }

                if (flags.NeedsExif)
                {
                    var exif = await ReadExifAsync(path, ct).ConfigureAwait(false);
                    if (exif is { } info)
                    {
                        exifDate = info.DateTimeOriginal?.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                        exifModel = info.Model;
                    }
                }
            }
        }

        var context = new NameContext(
            sourceName, baseName, extension, options.Start + index * options.Step,
            created, modified, exifDate, exifModel, options);

        var targetName = ComposeName(context, regex, options);

        item.TargetName = targetName;
        item.TargetPath = CombinePath(vfs, targetName);

        var validationError = ValidateName(targetName);
        if (validationError is not null)
        {
            item.IsValid = false;
            item.Error = validationError;
            return item;
        }

        item.IsValid = true;
        return item;
    }

    private static async Task MarkConflictsAsync(List<RenamePreviewItem> items, CancellationToken ct)
    {
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (!string.IsNullOrEmpty(item.SourcePath))
            {
                sources.Add(item.SourcePath);
            }
        }

        var byTarget = new Dictionary<string, List<RenamePreviewItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (!item.IsValid || IsNoOp(item))
            {
                continue;
            }

            if (!byTarget.TryGetValue(item.TargetPath, out var list))
            {
                byTarget[item.TargetPath] = list = [];
            }

            list.Add(item);
        }

        foreach (var pair in byTarget)
        {
            if (pair.Value.Count > 1)
            {
                foreach (var item in pair.Value)
                {
                    item.Conflict = true;
                    AppendError(item, "Another item in this batch targets the same name.");
                }

                continue;
            }

            var single = pair.Value[0];
            if (IsCaseOnlyRename(single))
            {
                // "photo.jpg" -> "Photo.jpg": the target is the source on a case-insensitive
                // volume, so existence says nothing. ApplyAsync renames through a temp name.
                continue;
            }

            if (sources.Contains(single.TargetPath))
            {
                // Occupied by an entry that this same batch renames away. The caller's ordering
                // decides whether that works; flagging it here would block legitimate shifts.
                continue;
            }

            ct.ThrowIfCancellationRequested();
            if (await ExistsSafeAsync(single.TargetPath, ct).ConfigureAwait(false))
            {
                single.Conflict = true;
                AppendError(single, "An item with this name already exists.");
            }
        }
    }

    // ---------------------------------------------------------------- name composition

    private readonly record struct NameContext(
        string SourceName,
        string BaseName,
        string ExtensionWithDot,
        int Count,
        DateTimeOffset Created,
        DateTimeOffset Modified,
        string? ExifDate,
        string? ExifModel,
        BatchRenameOptions Options);

    private static string ComposeName(in NameContext context, Regex? regex, BatchRenameOptions options)
    {
        var expanded = ExpandPattern(context);
        var extension = context.ExtensionWithDot;

        // The pattern owns the whole name, but forgetting the extension - or writing "{ext}"
        // without the dot separator, as in the default "{name}{ext}" - is the most common mistake
        // in a rename dialog. Restore the original extension in both cases.
        expanded = EnsureExtension(expanded, extension, options.IncludeExtensionInPattern);

        var (basePart, targetExtension) = SplitName(expanded);

        if (!string.IsNullOrEmpty(options.Find))
        {
            basePart = basePart.Replace(options.Find, options.Replace ?? string.Empty, StringComparison.Ordinal);
        }

        if (regex is not null)
        {
            basePart = regex.Replace(basePart, options.Replacement ?? string.Empty);
        }

        if (options.CaseMode != RenameCaseMode.Keep)
        {
            basePart = ApplyCase(basePart, options.CaseMode);
        }

        if (!string.IsNullOrEmpty(options.Prefix))
        {
            basePart = options.Prefix + basePart;
        }

        if (!string.IsNullOrEmpty(options.Suffix))
        {
            basePart += options.Suffix;
        }

        return basePart + targetExtension;
    }

    /// <summary>
    /// Makes sure the generated name carries the original extension. "{ext}" expands without the
    /// dot, so "photo" + "jpg" from the default pattern becomes "photo.jpg" and not "photojpg";
    /// a name that already ends in ".jpg" is left untouched.
    /// </summary>
    private static string EnsureExtension(string name, string extensionWithDot, bool includeInPattern)
    {
        if (includeInPattern || extensionWithDot.Length <= 1 || name.Length == 0)
        {
            return name;
        }

        if (name.EndsWith(extensionWithDot, StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        var bareExtension = extensionWithDot[1..];
        if (name.Length > bareExtension.Length &&
            name.EndsWith(bareExtension, StringComparison.OrdinalIgnoreCase) &&
            name[^(bareExtension.Length + 1)] != '.')
        {
            return name[..^bareExtension.Length] + extensionWithDot;
        }

        return name + extensionWithDot;
    }

    private static string ExpandPattern(in NameContext context)
    {
        var pattern = context.Options.Pattern;
        if (string.IsNullOrEmpty(pattern))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(pattern.Length + 16);
        var cursor = 0;
        while (cursor < pattern.Length)
        {
            var open = pattern.IndexOf('{', cursor);
            if (open < 0)
            {
                builder.Append(pattern, cursor, pattern.Length - cursor);
                break;
            }

            builder.Append(pattern, cursor, open - cursor);

            var close = pattern.IndexOf('}', open + 1);
            if (close < 0)
            {
                builder.Append(pattern, open, pattern.Length - open);
                break;
            }

            var token = pattern[(open + 1)..close];
            if (TryResolveToken(token, context, out var value))
            {
                builder.Append(value);
            }
            else
            {
                builder.Append('{').Append(token).Append('}');
            }

            cursor = close + 1;
        }

        return builder.ToString();
    }

    private static bool TryResolveToken(string token, in NameContext context, out string value)
    {
        var segments = token.Split('|');
        var resolved = ResolveKey(segments[0].Trim(), context);
        if (resolved is null)
        {
            value = string.Empty;
            return false;
        }

        for (var i = 1; i < segments.Length; i++)
        {
            resolved = ApplyTransform(resolved, segments[i].Trim());
        }

        value = resolved;
        return true;
    }

    private static string? ResolveKey(string key, in NameContext context)
    {
        var lower = key.ToLowerInvariant();
        switch (lower)
        {
            case "name":
                return context.Options.IncludeExtensionInPattern ? context.SourceName : context.BaseName;
            case "ext":
                return context.Options.IncludeExtensionInPattern
                    ? string.Empty
                    : context.ExtensionWithDot.TrimStart('.');
            case "orig":
                return context.SourceName;
            case "date":
                return DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            case "time":
                return DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
            case "created":
                return context.Created.ToLocalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            case "modified":
                return context.Modified.ToLocalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            case "exif:date":
                return context.ExifDate ??
                       context.Modified.ToLocalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            case "exif:model":
                return context.ExifModel ?? string.Empty;
            case "n":
                return FormatCount(context.Count, 0);
        }

        if (lower.StartsWith("n:", StringComparison.Ordinal))
        {
            var digits = key[2..].Trim();
            if (digits.Length is > 0 and <= 6 && digits.All(char.IsAsciiDigit))
            {
                // The literal is a digit count, not a number: "{n:000}" means "at least 3 digits".
                return FormatCount(context.Count, digits.Length);
            }
        }

        return null;
    }

    private static string FormatCount(int value, int width)
    {
        var digits = Math.Abs(value).ToString(CultureInfo.InvariantCulture);
        if (width > 0)
        {
            digits = digits.PadLeft(width, '0');
        }

        return value < 0 ? "-" + digits : digits;
    }

    private static string ApplyTransform(string value, string transformName)
    {
        return transformName.ToLowerInvariant() switch
        {
            "upper" => value.ToUpperInvariant(),
            "lower" => value.ToLowerInvariant(),
            "title" => ApplyCase(value, RenameCaseMode.Title),
            "trim" => value.Trim(),
            "slug" => ToSlug(value),
            _ => value
        };
    }

    private static string ApplyCase(string value, RenameCaseMode mode)
    {
        if (value.Length == 0)
        {
            return value;
        }

        switch (mode)
        {
            case RenameCaseMode.Upper:
                return value.ToUpperInvariant();
            case RenameCaseMode.Lower:
                return value.ToLowerInvariant();
            case RenameCaseMode.Title:
                var chars = value.ToCharArray();
                var atWordStart = true;
                for (var i = 0; i < chars.Length; i++)
                {
                    var c = chars[i];
                    if (char.IsLetter(c))
                    {
                        chars[i] = atWordStart ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c);
                        atWordStart = false;
                    }
                    else if (c is ' ' or '-' or '_' or '.' or '(' or '[')
                    {
                        atWordStart = true;
                    }
                }

                return new string(chars);
            default:
                return value;
        }
    }

    private static string ToSlug(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasDash = false;
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
                lastWasDash = false;
            }
            else if (!lastWasDash && builder.Length > 0)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        if (builder.Length > 0 && builder[^1] == '-')
        {
            builder.Length--;
        }

        return builder.ToString();
    }

    private static (string Base, string Extension) SplitName(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? (name, string.Empty) : (name[..dot], name[dot..]);
    }

    private static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "The new name is empty.";
        }

        if (name is "." or "..")
        {
            return "The new name cannot be '.' or '..'.";
        }

        foreach (var c in name)
        {
            if (c is '/' or '\\')
            {
                return "The new name cannot contain a path separator.";
            }

            if (char.IsControl(c))
            {
                return "The new name cannot contain control characters.";
            }

            if (Array.IndexOf(InvalidNameChars, c) >= 0)
            {
                return $"The new name contains the character '{c}', which is not allowed.";
            }
        }

        var byteCount = Encoding.UTF8.GetByteCount(name);
        if (byteCount > MaxNameBytes)
        {
            return $"The new name is {byteCount} bytes; the limit is {MaxNameBytes} bytes.";
        }

        if (OperatingSystem.IsWindows() && (name[^1] == ' ' || name[^1] == '.'))
        {
            return "Windows does not allow a name that ends with a space or a period.";
        }

        return null;
    }

    private static PatternFlags InspectPattern(string? pattern)
    {
        var needsStat = false;
        var needsExif = false;
        if (string.IsNullOrEmpty(pattern))
        {
            return new PatternFlags(false, false);
        }

        var cursor = 0;
        while (cursor < pattern.Length)
        {
            var open = pattern.IndexOf('{', cursor);
            if (open < 0)
            {
                break;
            }

            var close = pattern.IndexOf('}', open + 1);
            if (close < 0)
            {
                break;
            }

            var key = pattern[(open + 1)..close].Split('|')[0].Trim().ToLowerInvariant();
            if (key is "created" or "modified")
            {
                needsStat = true;
            }
            else if (key.StartsWith("exif:", StringComparison.Ordinal))
            {
                needsExif = true;
                needsStat = true;
            }

            cursor = close + 1;
        }

        return new PatternFlags(needsStat, needsExif);
    }

    private static Regex? BuildRegex(string? pattern, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(pattern))
        {
            return null;
        }

        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            error = "Invalid regular expression: " + ex.Message;
            return null;
        }
    }

    private static bool IsNoOp(RenamePreviewItem item)
        => string.Equals(item.TargetPath, item.SourcePath, StringComparison.Ordinal);

    private static bool IsCaseOnlyRename(RenamePreviewItem item)
        => !IsNoOp(item) && string.Equals(item.TargetPath, item.SourcePath, StringComparison.OrdinalIgnoreCase);

    private static string CombinePath(VfsPath path, string name)
    {
        try
        {
            return (path.Parent ?? path).Combine(name).Canonical;
        }
        catch (ArgumentException)
        {
            return name;
        }
    }

    private static void AppendError(RenamePreviewItem item, string message)
    {
        var text = string.IsNullOrWhiteSpace(message) ? "Rename failed." : message;
        item.Error = string.IsNullOrEmpty(item.Error) ? text : item.Error + " " + text;
    }

    // ---------------------------------------------------------------- apply helpers

    /// <summary>
    /// A case-only rename ("photo.jpg" -> "Photo.jpg") is a no-op for File.Move on macOS and
    /// Windows because both names resolve to the same entry. Hop through a unique temp name so
    /// the file system actually rewrites the casing.
    /// </summary>
    private static async Task RenameCaseOnlyAsync(
        RenamePreviewItem item, Func<string, string, Task<string>> renameAsync)
    {
        var tempName = ".fh-rename-" + Guid.NewGuid().ToString("N") + ".tmp";
        var tempPath = CombinePath(VfsPath.Parse(item.SourcePath), tempName);

        await renameAsync(item.SourcePath, tempName).ConfigureAwait(false);
        try
        {
            await renameAsync(tempPath, item.TargetName).ConfigureAwait(false);
        }
        catch
        {
            // Never leave an entry with a temp name: put the original back, then rethrow.
            try
            {
                await renameAsync(tempPath, item.SourceName).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort only; the caller reports the original failure.
            }

            throw;
        }
    }

    // ---------------------------------------------------------------- metadata helpers

    private static async Task<VfsStat> StatSafeAsync(string path, CancellationToken ct)
    {
        try
        {
            return await VfsFileSystem.Default.StatAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or ArgumentException)
        {
            return VfsStat.Missing;
        }
    }

    private static async Task<bool> ExistsSafeAsync(string path, CancellationToken ct)
    {
        var stat = await StatSafeAsync(path, ct).ConfigureAwait(false);
        return stat.Exists;
    }

    private static DateTimeOffset CreationTimeSafe(string localPath)
    {
        try
        {
            var created = File.GetCreationTime(localPath);
            return created == default ? DateTimeOffset.Now : new DateTimeOffset(created);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return DateTimeOffset.Now;
        }
    }

    // ---------------------------------------------------------------- EXIF

    private readonly record struct ExifInfo(DateTime? DateTimeOriginal, string? Model);

    /// <summary>
    /// Reads the JPEG APP1 / TIFF IFD for DateTimeOriginal and Model. System.Drawing is not
    /// available in this project and the desktop layer owns image decoding, so the few tags we
    /// need are parsed by hand. Any malformed or unsupported file simply yields null.
    /// </summary>
    private static async Task<ExifInfo?> ReadExifAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = await VfsFileSystem.Default.OpenReadAsync(path, ct).ConfigureAwait(false);
            var buffer = new byte[ExifMaxBytes];
            var total = 0;

            while (total < buffer.Length)
            {
                var want = Math.Min(ExifChunkBytes, buffer.Length - total);
                var read = await stream.ReadAsync(buffer.AsMemory(total, want), ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                total += read;
                if (total >= 2 && !LooksLikeJpegOrTiff(buffer))
                {
                    return null;
                }

                var info = ParseExif(buffer, total);
                if (info is not null)
                {
                    return info;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool LooksLikeJpegOrTiff(byte[] data)
        => (data[0] == 0xFF && data[1] == 0xD8) ||
           (data[0] == 'I' && data[1] == 'I') ||
           (data[0] == 'M' && data[1] == 'M');

    private static ExifInfo? ParseExif(byte[] data, int length)
    {
        if (length < 8)
        {
            return null;
        }

        int tiffOffset;
        if (data[0] == 0xFF && data[1] == 0xD8)
        {
            tiffOffset = FindApp1(data, length);
        }
        else if (LooksLikeJpegOrTiff(data))
        {
            tiffOffset = 0;
        }
        else
        {
            return null;
        }

        return tiffOffset < 0 ? null : ParseTiff(data, length, tiffOffset);
    }

    private static int FindApp1(byte[] data, int length)
    {
        var position = 2;
        while (position + 4 <= length)
        {
            if (data[position] != 0xFF)
            {
                position++;
                continue;
            }

            var marker = data[position + 1];
            if (marker == 0xFF)
            {
                position++;
                continue;
            }

            // Standalone markers carry no length field.
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                position += 2;
                continue;
            }

            if (marker is 0xDA or 0xD9)
            {
                return -1;
            }

            var segmentLength = (data[position + 2] << 8) | data[position + 3];
            if (segmentLength < 2)
            {
                return -1;
            }

            if (marker == 0xE1 && segmentLength >= 8 && position + 10 <= length &&
                data[position + 4] == 'E' && data[position + 5] == 'x' && data[position + 6] == 'i' &&
                data[position + 7] == 'f' && data[position + 8] == 0 && data[position + 9] == 0)
            {
                return position + 10;
            }

            position += 2 + segmentLength;
        }

        return -1;
    }

    private readonly record struct IfdEntry(ushort Tag, ushort Type, uint Count, int EntryOffset);

    private static ExifInfo? ParseTiff(byte[] data, int length, int tiff)
    {
        if (tiff + 8 > length)
        {
            return null;
        }

        var little = data[tiff] == 'I';
        if (!little && data[tiff] != 'M')
        {
            return null;
        }

        if (ReadU16(data, tiff + 2, little) != 42)
        {
            return null;
        }

        var ifd0 = (long)ReadU32(data, tiff + 4, little);
        var entries = ReadIfd(data, length, tiff, ifd0, little);
        if (entries is null)
        {
            return null;
        }

        string? model = null;
        string? fallbackDate = null;
        long exifIfd = 0;

        foreach (var entry in entries)
        {
            switch (entry.Tag)
            {
                case 0x0110:
                    model ??= ReadAsciiValue(data, length, tiff, entry, little);
                    break;
                case 0x0132:
                    fallbackDate ??= ReadAsciiValue(data, length, tiff, entry, little);
                    break;
                case 0x8769:
                    exifIfd = ReadInlineLong(data, entry, little);
                    break;
            }
        }

        string? originalDate = null;
        if (exifIfd > 0)
        {
            var exifEntries = ReadIfd(data, length, tiff, exifIfd, little);
            if (exifEntries is not null)
            {
                foreach (var entry in exifEntries)
                {
                    if (entry.Tag == 0x9003)
                    {
                        originalDate ??= ReadAsciiValue(data, length, tiff, entry, little);
                    }
                    else if (entry.Tag == 0x9004)
                    {
                        fallbackDate ??= ReadAsciiValue(data, length, tiff, entry, little);
                    }
                }
            }
        }

        var parsed = ParseExifDate(originalDate) ?? ParseExifDate(fallbackDate);
        if (parsed is null && string.IsNullOrEmpty(model))
        {
            return null;
        }

        return new ExifInfo(parsed, string.IsNullOrEmpty(model) ? null : model);
    }

    private static List<IfdEntry>? ReadIfd(byte[] data, int length, int tiff, long offset, bool little)
    {
        var start = tiff + (long)offset;
        if (offset <= 0 || start < 0 || start + 2 > length)
        {
            return null;
        }

        var count = ReadU16(data, (int)start, little);
        var entries = new List<IfdEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var entryOffset = (int)start + 2 + i * 12;
            if (entryOffset + 12 > length)
            {
                break;
            }

            entries.Add(new IfdEntry(
                ReadU16(data, entryOffset, little),
                ReadU16(data, entryOffset + 2, little),
                ReadU32(data, entryOffset + 4, little),
                entryOffset));
        }

        return entries;
    }

    private static string? ReadAsciiValue(byte[] data, int length, int tiff, IfdEntry entry, bool little)
    {
        const ushort ascii = 2;
        if (entry.Type != ascii || entry.Count == 0)
        {
            return null;
        }

        var count = (int)Math.Min(entry.Count, 4096);
        int start;
        if (entry.Count <= 4)
        {
            start = entry.EntryOffset + 8;
        }
        else
        {
            var valueOffset = ReadU32(data, entry.EntryOffset + 8, little);
            start = tiff + (int)valueOffset;
        }

        if (start < 0 || start + count > length)
        {
            return null;
        }

        var end = start;
        while (end < start + count && data[end] != 0)
        {
            end++;
        }

        var text = Encoding.UTF8.GetString(data, start, end - start).Trim();
        return text.Length == 0 ? null : text;
    }

    private static long ReadInlineLong(byte[] data, IfdEntry entry, bool little)
    {
        // ExifIFD pointer: type LONG, count 1, value stored inline.
        if (entry.Type != 4 || entry.Count < 1)
        {
            return 0;
        }

        return ReadU32(data, entry.EntryOffset + 8, little);
    }

    private static DateTime? ParseExifDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        string[] formats = ["yyyy:MM:dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss"];
        return DateTime.TryParseExact(trimmed, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value
            : null;
    }

    private static ushort ReadU16(byte[] data, int offset, bool little)
        => little
            ? (ushort)(data[offset] | (data[offset + 1] << 8))
            : (ushort)((data[offset] << 8) | data[offset + 1]);

    private static uint ReadU32(byte[] data, int offset, bool little)
        => little
            ? (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24))
            : (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
}
