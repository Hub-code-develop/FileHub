// Marketplace query DTO. Data is fetched from the public Visual Studio Marketplace Gallery at
// the user's request; no extension package is ever bundled with FileHub.
// "Visual Studio Marketplace" and "Visual Studio Code" are trademarks of Microsoft Corporation.
// FileHub is not affiliated with or endorsed by Microsoft.

namespace FileHub.Code.Models;

/// <summary>A single extension listed by the Visual Studio Marketplace.</summary>
public sealed class MarketplaceExtension
{
    /// <summary>Fully qualified id: <c>publisher.name</c>.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public string PublisherDisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string TargetPlatform { get; init; } = string.Empty;
    public string IconUrl { get; init; } = string.Empty;
    public string LicenseUrl { get; init; } = string.Empty;
    public string VsixUrl { get; init; } = string.Empty;

    public string MarketplaceUrl => $"https://marketplace.visualstudio.com/items?itemName={Uri.EscapeDataString(Id)}";

    public long InstallCount { get; init; }
    public double Rating { get; init; }
    public long RatingCount { get; init; }
    public bool IsVerifiedPublisher { get; init; }

    /// <summary>False when the selected version has no package for the current OS/architecture.</summary>
    public bool IsCompatible { get; init; } = true;

    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;
}
