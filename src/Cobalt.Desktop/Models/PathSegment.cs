namespace Cobalt.Desktop.Models;

public sealed class PathSegment
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }

    public override string ToString() => Name;
}
