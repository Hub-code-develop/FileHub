# Third-Party Notices

FileHub contains material derived from third-party open-source projects. This file records
every such project, what was used, and the license terms that apply.

---

## Files (Files App)

- **Project:** Files — a Windows file manager built with WinUI 3
- **Source:** https://github.com/files-community/Files
- **License:** MIT License
- **Copyright:** Copyright (c) 2018 - present Files Community
- **Status:** **Derived work.** UI structure and layout code were ported, see below.

### What was ported

The user interface of FileHub is not merely "inspired by" Files — its XAML layout was
**ported** from the Files repository and then re-expressed for Avalonia. The following source
files were read and their structure, grid partitions, row/column definitions, control sizes,
corner radii, spacing and command groupings were carried over:

| Files source file | Ported into |
| --- | --- |
| `src/Files.App/Views/MainPage.xaml` | `src/FileHub.Desktop/Views/MainWindow.axaml` (root grid: tab bar / address bar / sidebar + content / status bar) |
| `src/Files.App/UserControls/NavigationToolbar.xaml` | `MainWindow.axaml` row 1 (48px toolbar, 36x32 icon buttons, omnibar) |
| `src/Files.App/UserControls/Toolbar.xaml` | `MainWindow.axaml` command card (rounded 8px command bar, label-right buttons, Layout / Sort / Details / Hidden groupings) |
| `src/Files.App/UserControls/TabBar/TabBar.xaml` | `MainWindow.axaml` row 0 (`FATabView` strip, 34px height, footer actions) |
| `src/Files.App/UserControls/StatusBar.xaml` | `MainWindow.axaml` row 3 (32px status bar, 1x18 dividers) |
| `src/Files.App/UserControls/Pane/InfoPane.xaml` | `MainWindow.axaml` InfoPane (300px pane, tab pills, 90px label column) |
| `src/Files.App/Views/Layouts/DetailsLayoutPage.xaml` | `MainWindow.axaml` column header (40px) and 34/*/110/150/160 column grid |
| `src/Files.App/Styles/TabBarStyles.xaml` | `src/FileHub.Desktop/Styles/FileHubTheme.axaml` |

The port is not a copy-paste: WinUI 3 XAML does not run on Avalonia. WinUI controls were mapped
to CodeHub-FluentUI and Avalonia equivalents, and Files-only concepts (Shell COM, `x:Bind`,
`ThemeResource`, `NavigationView`) were replaced by FileHub view models. Files-specific
controls that have no library equivalent — `SidebarView`, `CommandBar`, `InfoPane`,
`StatusBar` — were re-implemented in `src/FileHub.Desktop/Controls/FileHubControls.cs`.

Because the layout structure was ported rather than independently designed, the original
copyright notice and permission notice are reproduced in full below, as the MIT License
requires for substantial portions of the work.

### MIT License (Files)

```
MIT License

Copyright (c) 2018 - present Files Community

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### Note on LICENSE-MPL

The Files repository also ships a `LICENSE-MPL` file (Mozilla Public License 2.0). In that
repository it covers specific third-party components, not the application source as a whole.
No MPL-covered file was used by this project, so no MPL obligations attach to FileHub. The
file is mentioned here only for completeness.

---

## CodeHub-FluentUI (FluentUI-CodeHub)

- **Project:** CodeHub-FluentUI — a FluentAvalonia fork maintained by the CodeHub organization
- **Source:** https://github.com/Hub-code-develop/CodeHub-FluentUI
- **License:** MIT
- **Status:** **Binary dependency.** Referenced as a NuGet package; no source was modified.

The package `CodeHub-FluentUI 3.1.0-codehub.1` is vendored in `local-feed/` so the repository
restores offline. See `local-feed/README.md`.

---

## FluentAvalonia (upstream of CodeHub-FluentUI)

- **Project:** FluentAvalonia
- **Source:** https://github.com/amwx/FluentAvalonia
- **License:** MIT
- **Status:** **Indirect binary dependency** via CodeHub-FluentUI.

---

## Avalonia

- **Project:** Avalonia UI
- **Source:** https://github.com/AvaloniaUI/Avalonia
- **License:** MIT
- **Status:** **Binary dependency.** Referenced as NuGet packages.

---

## .NET Runtime

- **Project:** .NET
- **Source:** https://github.com/dotnet/runtime
- **License:** MIT
- **Status:** **Runtime dependency.**

---

## Fonts and icons

FileHub uses the Segoe Fluent Icons / Symbol icon font through FluentAvalonia's `FASymbolIcon`.
The font is provided by the operating system or by the FluentAvalonia package; no font file is
redistributed in this repository.
