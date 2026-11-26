# Third-Party Notices

FileHub contains material derived from third-party open-source projects. This file records
every such project, what was used, and the license terms that apply.

## Redistribution / derived-work statement

FileHub is a **derived work** of the projects listed below. Its file-browsing interface,
command structure and control styles were ported from the Files App and WinUI 3 projects.
Both upstreams are MIT-licensed: the MIT License explicitly permits use, copying,
modification, merging, publication, **redistribution**, sublicensing and sale, provided the
original copyright notice and permission notice are included in all copies or substantial
portions of the work. Those notices are reproduced verbatim below (see "MIT License (Files)"
and "MIT License (Microsoft)"). This file, together with `LICENSE`, must accompany any copy
or substantial portion of FileHub that is redistributed, in source or binary form. FileHub's
own changes are released under the MIT License in `LICENSE`; that license does not and cannot
override the upstream copyrights recorded here.

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
| `src/Files.App/UserControls/NavigationToolbar.xaml` | `Views/Controls/FilesNavigationToolbar.axaml(.cs)` (48px toolbar, 36x32 icon buttons, omnibar, exposed nav buttons) |
| `src/Files.App/UserControls/Toolbar.xaml` | `Views/Controls/FilesToolbar.axaml(.cs)` (8px card, two command bars: context commands left, view commands right, label-right AppBarButtons; buttons created from the command registry) |
| `src/Files.App/UserControls/TabBar/TabBar.xaml` | `MainWindow.axaml` row 0 (WinUI 3 tab strip `WinTabStrip`, 32px tabs, footer actions) |
| `src/Files.App/UserControls/StatusBar.xaml` | `Views/Controls/FilesStatusBar.axaml(.cs)` (32px status bar, 1x18 dividers, item count / selection count / size, view actions) |
| `src/Files.App/UserControls/Pane/InfoPane.xaml` | `Views/Controls/FilesInfoPane.axaml(.cs)` (300px pane, tab pills, 90px label column, 2*/3* preview/properties rows) |
| `src/Files.App.Controls/Sidebar/SidebarView.xaml` | `Views/Controls/FilesSidebar.axaml(.cs)` (header / menu items / footer rows, resizer) |
| `src/Files.App/Views/Layouts/DetailsLayoutPage.xaml` | `Views/Controls/DetailsLayoutPage.axaml(.cs)` (40px column header, 34/*/110/150/160 column grid, compact rows, loading skeleton) |
| `src/Files.App/UserControls/Pane/InfoPane.xaml` (Details / Preview tabs) | `Views/Controls/FilesInfoPane.axaml(.cs)` — the tab-pill header and preview/property split were carried over; FileHub reimplemented the preview content because Files uses WinRT thumbnails |
| `src/Files.App/Actions/IAction.cs`, `src/Files.App/Actions/BaseUIAction.cs` | `src/FileHub.Desktop/Actions/` (`IAction` → `IRichCommand`, `BaseUIAction` → `RichCommand`) |
| `src/Files.App/Data/Commands/Manager/CommandManager.cs` | `Actions/CommandManager.cs` (`CommandCodes` identity enum, command registry indexed by code/hotkey, context/primary/secondary groups) |
| `src/Files.App/Styles/TabBarStyles.xaml` | `src/FileHub.Desktop/Styles/FileHubTheme.axaml` |

The port is not a copy-paste: WinUI 3 XAML does not run on Avalonia. WinUI controls were mapped
to CodeHub-FluentUI and Avalonia equivalents, and Files-only concepts (Shell COM, `x:Bind`,
`ThemeResource`, WinRT storage, `ItemsRepeater`) were replaced by FileHub view models and the
cross-platform `FileSystemService`. Files-specific controls with no library equivalent were
re-implemented under `Views/Controls/`. Files' command set depends on WinRT/Win32 code that
cannot execute on macOS or Linux; its *structure* (identifier enum, registry, command objects
carrying label/glyph/hotkey/executable state) was ported, while the command bodies call
FileHub's cross-platform services.

Because the layout structure was ported rather than independently designed, the original
copyright notice and permission notice are reproduced in full below, as the MIT License
requires for substantial portions of the work.

### Thumbnails and previews: structure ported, mechanism reimplemented

Files App generates thumbnails through WinRT (`StorageFile.GetThumbnailAsync`, the
`Files.App.FileSystem` thumbnail helpers and platform caches). Those APIs are Windows-only, so
FileHub reimplemented the *behaviour* with its own cross-platform code: `PreviewService` probes
the file extension in the logic-only `FileHub.Files` project, and `ThumbnailService` on the
desktop side decodes bitmaps off the UI thread with Avalonia's Skia-backed
`Bitmap.DecodeToWidth` and keeps a bounded cache. The list / grid image wells and the details
pane's two-tab layout follow the Files port listed above. No Files thumbnail source code was
translated, and no upstream binary assets are bundled.

### Drag and drop / directory watcher: behaviour reimplemented

Files App implements drag-and-drop and `IFileSystemWatcher` through WinRT / Windows Shell APIs.
Those do not exist on macOS or Linux, so FileHub reimplemented both behaviours independently:

- `Desktop/Services/DragDropPayload.cs` defines a private Avalonia `DataFormat<string>` carrying a
  JSON path list, with `DataFormat.File` and plain text as cross-application fallbacks. The drop
  targets (folder rows, breadcrumb segments, sidebar locations) and the drag-over highlight are
  FileHub's own code in `Views/MainWindow.axaml.cs` and `Styles/AppStyles.axaml`. The Ctrl-to-copy
  gesture follows the same user-facing convention Files uses, but no Files drag-drop source was
  translated.
- `FileHub.Files/Services/DirectoryWatcherService.cs` wraps .NET's cross-platform
  `FileSystemWatcher` with a 280 ms coalescing debounce and error recovery. The per-tab
  refresh-and-preserve-selection logic in `MainViewModel` is FileHub's own implementation.

No Files drag-drop or watcher source code was translated, and no upstream binary assets are
bundled for these features.

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

## WinUI 3 (Microsoft.UI.Xaml)

- **Project:** Windows UI Library (WinUI 3), part of the Windows App SDK
- **Source:** https://github.com/microsoft/microsoft-ui-xaml (`main` branch, `controls/dev/CommonStyles/`)
- **License:** MIT License
- **Copyright:** Copyright (c) Microsoft Corporation
- **Status:** **Derived work.** Default control styles were ported to Avalonia, see below.

### What was ported

`src/WinUI3.Fluent/` is a from-source port of the WinUI 3 default control styles. The upstream
XAML was read directly from the repository above — no decompilation was involved, because the
style definitions ship as plain-text XAML:

| WinUI 3 source file | Ported into |
| --- | --- |
| `Common_themeresources_any.xaml` | `src/WinUI3.Fluent/Themes/Tokens.axaml` (Light and Dark colour palettes, every hex value copied verbatim) |
| `CornerRadius_themeresources.xaml` | `Tokens.axaml` (`ControlCornerRadius` 4, `OverlayCornerRadius` 8) |
| `Button_themeresources.xaml` | `Themes/Button.axaml` (padding `11,5,11,6`, 1px elevation gradient border, `AccentButtonStyle`, `SubtleButtonStyle`) |
| `ToggleButton_themeresources.xaml` | `Themes/ToggleButton.axaml` (checked state fills with the accent colour) |
| `CheckBox_themeresources.xaml` | `Themes/CheckBox.axaml` (20px box, 1px border, 8px label gap, check mark path) |
| `RadioButton_themeresources.xaml` | `Themes/RadioButton.axaml` (20px circle, 10px on-accent dot when checked) |
| `ListViewItem_themeresources.xaml` | `Themes/ListBoxItem.axaml` (hover surface, 3x16 accent selection pill) |
| `ToolTip_themeresources.xaml` | `Themes/Surfaces.axaml` (12px text, padding `9,6,9,8`) |
| `FlyoutPresenter_themeresources.xaml` | `Themes/Surfaces.axaml` (8px corner radius, 1px flyout stroke) |
| `AppBarButton_themeresources.xaml` | `Themes/AppBarButton.axaml` + `Controls/WinAppBarButton.cs` (48px row, 2,6 inner-border inset, 16px icon, 12px label, right/collapsed/bottom label positions) |
| `TabView/TabView_themeresources.xaml`, `TabView/TabViewItem_themeresources.xaml` | `Themes/TabStrip.axaml` + `Controls/WinTabStrip.cs` (32px tab, 8,8,0,0 corners, 100/240 min/max width, close button overlay) |
| `TextBox_themeresources.xaml` | `Themes/TextBox.axaml` (32px height, `10,5,6,6` padding, 2px accent underline on focus) |
| `ToggleSwitch_themeresources.xaml` | `Themes/ToggleSwitch.axaml` (40x20 track, 12px knob, 167 ms travel) |

What came across: the exact colour ramps for light and dark, the type ramp, the control
geometries (32px control height, 4px/8px corner radii, paddings, 40px list rows), the state
colour mapping (normal / pointer-over / pressed / disabled / checked), the 83 ms background
fade (`BrushTransition 0:0:0.083`), and the gradient `ControlElevationBorderBrush` that gives
Fluent controls their lit top edge.

What did not come across:

- **Acrylic and Mica.** Both are DWM composition backdrops with no portable Avalonia
  equivalent. Tooltips and flyouts fall back to the solid `ControlSolidFillColorDefault` /
  `LayerFillColorDefault` surfaces.
- **Segoe UI Variable.** Not licensed for redistribution. The font list falls through to
  whatever the host provides, so text metrics differ by a pixel or two.
- **`Windows.UI.Composition` animations.** Spring and implicit animations are approximated with
  Avalonia transitions; easing curves are close but not identical.
- Controls that depend on Windows-only infrastructure (AnimatedIcon, MediaPlayerElement,
  WebView2, InkCanvas, ThemeShadow) are out of scope.
- **ScrollBar is inherited, not ported.** Avalonia's `ScrollBar` takes over `PART_Track` in
  `OnApplyTemplate` and computes the thumb from `ViewportSize`; a straight copy of WinUI's
  template degrades the thumb to a dot. Rather than ship a broken bar, the FluentAvalonia
  scroll bar stays in place. `Themes/ScrollBar.axaml` is therefore intentionally absent.
- `NavigationView`, `BreadcrumbBar`, `InfoBar`, `ProgressRing`, `MenuFlyout` and `ComboBox` are
  still served by CodeHub-FluentUI. Those are the largest templates in WinUI 3 (NavigationView
  alone is several thousand lines) and they are visually already very close; see
  "Control coverage" in README.md for the current status of each.

The remaining visual difference against a real WinUI 3 window is therefore concentrated in the
font, the translucent backdrops and sub-pixel text rendering — not in the control shapes.

---

## MIT License (Microsoft)

The WinUI 3 styles reproduced in `src/WinUI3.Fluent/` are covered by:

```
MIT License

Copyright (c) Microsoft Corporation.

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

## monaco-editor (code editor)

- **Project:** monaco-editor, version 0.52.2
- **Source:** https://github.com/microsoft/monaco-editor
- **License:** MIT License
- **Copyright:** Copyright (c) 2016 - present Microsoft Corporation
- **Status:** **Redistributed verbatim.** The minified `min/vs` directory (103 files, including
  `editor.main.js`, `editor.main.css`, `loader.js`, basic language definitions and the web worker)
  is bundled under `src/FileHub.Desktop/Assets/Monaco/vs/` and copied to the application output.
  No upstream source file was modified.
- The full upstream licence is kept at `licenses/Monaco/LICENSE`, and the upstream list of
  components bundled inside monaco-editor is kept at `licenses/Monaco/ThirdPartyNotices.txt`.
  Both must accompany any redistribution of FileHub.

### MIT License (monaco-editor)

```
The MIT License (MIT)

Copyright (c) 2016 - present Microsoft Corporation

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

---

## Avalonia Controls WebView

- **Project:** Avalonia.Controls.WebView, version 12.1.2
- **Source:** https://github.com/AvaloniaUI/Avalonia.Controls.WebView
- **License:** MIT License
- **Copyright:** Copyright 2019-2026 © AvaloniaUI OÜ
- **Status:** **Binary dependency.** Referenced as a NuGet package; no source was modified. It
  wraps WebView2 (Windows), WKWebView (macOS) and WebKit / WPE WebKit (Linux).

---

## VS Code extension data and Visual Studio Marketplace

The Extensions page reads `package.json`, colour themes and snippet files from extensions the
user has already installed locally. Those files are **user data, not redistributed with FileHub**
and are not derived works of FileHub. Their own licences remain with their respective authors.

The Marketplace tab queries the public Visual Studio Marketplace Gallery endpoint
(`https://marketplace.visualstudio.com/_apis/public/gallery/extensionquery`) at the user's
request and may download a VSIX package. Search terms are sent to Microsoft. Downloaded packages
are extracted into FileHub's own extension folder
(`ApplicationData/FileHub/extensions`), are **never bundled with FileHub or committed to this
repository**, and remain subject to each extension's own licence. The extensions page can open
the Marketplace listing and licence URL so the user can review those terms before installing.

"Visual Studio Marketplace", "Visual Studio Code" and "VS Code" are trademarks of Microsoft
Corporation. FileHub is **not** an official Microsoft client and is not affiliated with or
endorsed by Microsoft. The Gallery endpoint is an internal API without a public SLA and may
change or rate-limit; the feature degrades gracefully when it is unavailable.

FileHub implements contribution *consumption* itself: file-extension to language-id mapping,
VS Code theme to Monaco theme conversion, and snippet completion registration. It does not
include, translate or execute any VS Code extension source code, and it is not a VS Code
extension host. Installed packages are not executed: only declarative manifests, themes and
snippets are read.

---

## Fonts and icons

FileHub uses the Segoe Fluent Icons / Symbol icon font through FluentAvalonia's `FASymbolIcon`.
That font is provided by the operating system or by the FluentAvalonia package; it is not
redistributed in this repository.

The bundled monaco-editor assets do include one font file, `codicon.ttf`, used by Monaco's own
editor UI. It is redistributed verbatim as part of monaco-editor under the MIT license above and
is covered by `licenses/Monaco/ThirdPartyNotices.txt`.
