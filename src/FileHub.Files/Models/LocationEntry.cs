namespace FileHub.Files.Models;

public enum LocationKind
{
    QuickAccess,
    Drive
}

/// <summary>左侧导航里的一个位置（快速访问或磁盘挂载点）。</summary>
public sealed class LocationEntry
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string IconKey { get; init; }
    public LocationKind Kind { get; init; }

    public string? Detail { get; init; }

    public override string ToString() => Name;
}
