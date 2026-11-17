# Bundled offline NuGet feed

`CodeHub-FluentUI.3.1.0-codehub.1.nupkg` comes from
[`Hub-code-develop/CodeHub-FluentUI`](https://github.com/Hub-code-develop/CodeHub-FluentUI)
(a CodeHub fork of FluentAvalonia 3.x, **MIT** licensed). That version is not published on
nuget.org, so the package is committed here to make `dotnet restore` / `./build.sh` work
immediately after cloning.

- Target framework: `net10.0`; depends on Avalonia `12.1.0`
- The root `NuGet.Config` references this directory through the relative path `local-feed`;
  no machine-specific configuration is needed
- If the package is published on nuget.org later, remove the `codehub-local` source from
  `NuGet.Config` and delete this directory
- To upgrade: replace the nupkg here and update the `PackageReference` version in
  `src/FileHub.Desktop/FileHub.Desktop.csproj`

This documentation file is in English, which is the default language for documentation.
The application UI follows the operating system language instead; see the
[README](../README.md#ui-language).
