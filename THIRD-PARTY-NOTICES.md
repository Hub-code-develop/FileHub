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
| `src/Files.App/UserControls/TabBar/TabBar.xaml` | `MainWindow.axaml` row 0 (WinUI 3 tab strip `WinTabStrip`, 32px tabs, footer actions) |
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
