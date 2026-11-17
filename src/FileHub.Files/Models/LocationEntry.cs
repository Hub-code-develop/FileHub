namespace FileHub.Files.Models;

public enum LocationKind
{
    QuickAccess,
    Drive
}

/// <summary>A location in the sidebar (quick access item or mounted drive).</summary>
public sealed class LocationEntry
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string IconKey { get; init; }

    /// <summary>Localization key for Name; null for drives (whose name is already display-ready).</summary>
    public string? NameKey { get; init; }
    public LocationKind Kind { get; init; }

    public string? Detail { get; init; }

    public override string ToString() => Name;
}
