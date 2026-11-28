# FileHub

[![Build](https://github.com/Hub-code-develop/FileHub/actions/workflows/build.yml/badge.svg)](https://github.com/Hub-code-develop/FileHub/actions/workflows/build.yml)

[Chinese README (中文版说明)](README.zh-CN.md)

A cross-platform file manager built with **.NET 10 + Avalonia 12 + FluentUI-CodeHub
(`CodeHub-FluentUI`)**. Its user interface is **ported from**
[Files App](https://github.com/files-community/Files), the WinUI 3 file manager for Windows.

This English file is the primary README. The Chinese translation is
[README.zh-CN.md](README.zh-CN.md). Documentation defaults to English; the application UI does
not (see [UI language](#ui-language)).

![FileHub main window](docs/filehub-main.png)

## Attribution: code ported from Files

**The UI of FileHub is a port of [Files](https://github.com/files-community/Files), not merely
an imitation.** Its XAML layout structure was carried over from that repository and re-expressed
with Avalonia controls.

| Files source (MIT) | Ported to in FileHub |
| --- | --- |
| `src/Files.App/Views/MainPage.xaml` | `src/FileHub.Desktop/Views/MainWindow.axaml` root grid |
| `src/Files.App.Controls/Sidebar/SidebarView.xaml` | `Views/Controls/FilesSidebar.axaml(.cs)` — header / items / footer |
| `src/Files.App/UserControls/NavigationToolbar.xaml` | `Views/Controls/FilesNavigationToolbar.axaml(.cs)` — 48px, 36x32 buttons |
| `src/Files.App/UserControls/Toolbar.xaml` | `Views/Controls/FilesToolbar.axaml(.cs)` — 8px card, buttons built from the command registry |
| `src/Files.App/UserControls/TabBar/TabBar.xaml` | `WinTabStrip` tab strip (30px tabs, window actions) |
| `src/Files.App/UserControls/StatusBar.xaml` | `Views/Controls/FilesStatusBar.axaml(.cs)` — 32px, 1x18 dividers |
| `src/Files.App/UserControls/Pane/InfoPane.xaml` | `Views/Controls/FilesInfoPane.axaml(.cs)` — 300px pane, tab pills |
| `src/Files.App/Views/Layouts/DetailsLayoutPage.xaml` | `Views/Controls/DetailsLayoutPage.axaml(.cs)` — 40px header, 34/*/110/150/160 columns |
| `src/Files.App/Actions/IAction.cs`, `BaseUIAction.cs`, `Data/Commands/Manager/CommandManager.cs` | `src/FileHub.Desktop/Actions/` — `CommandCodes`, `IRichCommand`, `RichCommand`, `CommandManager` |
| `src/Files.App/Styles/TabBarStyles.xaml` | `src/FileHub.Desktop/Styles/FileHubTheme.axaml` |

What came across: the shell skeleton (tab strip, grouped sidebar with quick access and drives,
navigation toolbar, command bar, file area with a toggleable details pane, status bar), the
command groupings (Layout / Sort / Details pane / Hidden items), and the concrete layout
numbers (row heights, column widths, corner radii, paddings, spacing).

What did not come across: WinUI 3 does not run on Avalonia, so every control was mapped to a
CodeHub-FluentUI or Avalonia equivalent, and Files-only infrastructure (Shell COM, `x:Bind`,
`ThemeResource`, WinRT storage, `ItemsRepeater`) was replaced by FileHub view models and the
cross-platform `FileSystemService`. Files' command bodies depend on WinRT/Win32 and cannot run
on macOS or Linux; the command *structure* (identifier enum, registry, rich command objects)
was ported, while the bodies call FileHub's cross-platform services.

**Files is MIT licensed, and because its layout code was ported the original copyright and
permission notice are reproduced in full in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).**
That file also lists every other third-party component.

FileHub itself is released under the [MIT License](LICENSE).
Thanks to the Files Community for a genuinely well-designed file manager.

## Attribution: control styles ported from WinUI 3

`src/WinUI3.Fluent/` is a **from-source port of the WinUI 3 default control styles**
([microsoft/microsoft-ui-xaml](https://github.com/microsoft/microsoft-ui-xaml), MIT). The
upstream style definitions ship as plain-text XAML, so every colour, corner radius, padding and
state colour in that project was copied from
`controls/dev/CommonStyles/*_themeresources.xaml` rather than eyeballed from screenshots.

| WinUI 3 source (MIT) | Ported to in FileHub |
| --- | --- |
| `Common_themeresources_any.xaml` | `src/WinUI3.Fluent/Themes/Tokens.axaml` — light and dark colour palettes |
| `CornerRadius_themeresources.xaml` | `Tokens.axaml` — `ControlCornerRadius` 4, `OverlayCornerRadius` 8 |
| `Button_themeresources.xaml` | `Themes/Button.axaml` — 32px height, `11,5,11,6` padding, accent and subtle variants |
| `ToggleButton_themeresources.xaml` | `Themes/ToggleButton.axaml` |
| `CheckBox_themeresources.xaml` | `Themes/CheckBox.axaml` — 20px box, 8px label gap |
| `RadioButton_themeresources.xaml` | `Themes/RadioButton.axaml` — accent disc plus on-accent dot |
| `ListViewItem_themeresources.xaml` | `Themes/ListBoxItem.axaml` — hover surface and 3x16 selection pill |
| `TextBox_themeresources.xaml` | `Themes/TextBox.axaml` — 32px, `10,5,6,6` padding, accent underline on focus |
| `ToggleSwitch_themeresources.xaml` | `Themes/ToggleSwitch.axaml` — 40x20 track, 12px knob |
| `ToolTip_themeresources.xaml`, `FlyoutPresenter_themeresources.xaml` | `Themes/Surfaces.axaml` |
| `TabView` / `TabViewItem` | `Themes/TabStrip.axaml` + `Controls/WinTabStrip.cs` — 32px tab, `8,8,0,0` corners |
| `AppBarButton` / `CommandBar` | `Themes/AppBarButton.axaml` + `Controls/WinAppBarButton.cs` — 48px row, `2,6` inner inset, 12px label |

### Control coverage

| Control | Status |
| --- | --- |
| Button, ToggleButton, CheckBox, RadioButton, ToggleSwitch, TextBox, TextBlock | Ported |
| AppBarButton (command bar buttons) | Ported |
| ListBoxItem / ListViewItem | Ported |
| ToolTip, FlyoutPresenter | Ported |
| TabView (tab strip) | Ported, including the close button, middle-click close, drag reorder, context menu and overflow scroll |
| ScrollBar | Inherited — see below |
| NavigationView, BreadcrumbBar, InfoBar, ProgressRing, MenuFlyout, ComboBox | Served by CodeHub-FluentUI |

`ScrollBar` is the one deliberate omission. Avalonia's `ScrollBar` takes ownership of
`PART_Track` in `OnApplyTemplate` and derives the thumb from `ViewportSize`; copying WinUI's
template through verbatim collapses the thumb to a dot. A working Fluent scroll bar beats a
faithful broken one.

The port is deliberately honest about its limits: Acrylic and Mica are Windows compositor
backdrops with no portable equivalent, and Segoe UI Variable is not licensed for redistribution.
A WinUI 3 window and this port therefore differ in font rendering and translucency, not in
control geometry. Full detail in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

`CodeHub-FluentUI` is a separate dependency from
[`Hub-code-develop/CodeHub-FluentUI`](https://github.com/Hub-code-develop/CodeHub-FluentUI)
(MIT); its offline package is described in [local-feed/README.md](local-feed/README.md).

## UI language

The application detects the **operating system language at startup**. English is never a
hard-coded default for the UI. A Chinese system shows Chinese; an English system shows English.

Detection order (first usable signal wins):

| Platform | Signal |
| --- | --- |
| macOS | `defaults read -g AppleLanguages`, then `AppleLocale` |
| Windows | `CultureInfo.InstalledUICulture` |
| Linux / Unix | `LC_ALL`, then `LC_MESSAGES`, then `LANG` (`C`, `POSIX`, `C.UTF-8` are skipped because they carry no language) |
| Any | `CultureInfo.CurrentUICulture` as a last resort |

The macOS preference is queried first on purpose: a GUI app launched from Finder inherits no
`LC_*` variables, so reading `AppleLanguages` is the only reliable way to get the user's real
language. The tooltip on the language button shows which signal was used, for example
`zh-Hans-CN / macOS AppleLanguages`.

- `FILEHUB_LANG=en`, `FILEHUB_LANG=zh-CN` or `FILEHUB_LANG=auto` overrides detection for a
  single run. `auto` restores OS detection.
- The in-app language button cycles **Auto -> English -> Chinese -> Auto**. A manual choice is
  only kept for the current session; the next launch follows the OS again.
- English and Simplified Chinese are the two available translations. Any other locale falls back
  to English only because no translation exists for it yet, not because English is preferred.
- Only documentation defaults to English. Every user-visible string in the app has both English
  and Chinese values in `Services/LocalizationService.cs`.

## Tech stack

| Component | Version / source |
| --- | --- |
| .NET | `net10.0` (SDK 10.0.400) |
| Avalonia | 12.1.0 |
| UI style library | `CodeHub-FluentUI` `3.1.0-codehub.1` (bundled offline feed in `local-feed/`) |
| Theme | `FluentAvaloniaTheme` (`FluentAvalonia.Styling`) |

FluentUI-CodeHub widgets used: `FANavigationView` (sidebar), `FABreadcrumbBar` (path),
`FAContentDialog` (new / rename / delete confirmation / properties), `FAInfoBar` (feedback),
`FAProgressRing` and `FASkeleton` (loading state, the latter is specific to the CodeHub fork),
`FAMenuFlyout` / `FAMenuFlyoutItem` (context menu), `FASymbolIcon` / `FASymbolIconSource` (icons).

## Features

- Sidebar lists platform quick access (Home, Desktop, Downloads, Documents, Pictures, Videos)
  and mounted drives
- Back / Forward / Up / Refresh with navigation history
- Breadcrumb path plus an editable address bar (`~` supported)
- Details view (Name / Size / Type / Date modified, click a column header to sort) and icon grid view
- **Image thumbnails and file previews**: JPG / PNG / GIF / BMP / WebP / TIFF show a decoded
  thumbnail in both views; selecting a file shows a large image preview or a text prefix in the
  details pane. The pane's **Details** / **Preview** tabs switch between properties and the
  full preview. Unsupported formats fall back to a category icon.
- Live filtering plus recursive search (300 result limit, cancellable)
- File operations: open, reveal in system file manager, new folder, rename, copy / cut / paste,
  copy path, properties
- **Drag and drop**: drag selected items onto a folder row, a breadcrumb segment or a sidebar
  location to move them; hold Ctrl to copy instead. Dropping files from Finder / Explorer
  imports them as a copy. Name collisions are resolved with a numeric suffix, never overwritten
- **Live directory watcher**: external creates, deletes and renames in the current folder refresh
  the list automatically (coalesced, 280 ms debounce), preserving the current selection. Manual
  refresh (F5) remains available for volumes that cannot be watched
- **Chromeless window, tabs as the title bar**: on macOS the native title bar is gone. The client
  area reaches the window edge and the tab strip does the title bar's job — drag it to move the
  window, double-click to zoom, scroll the wheel over it to flip through tabs. The traffic lights
  keep their own reserved inset. Windows keeps its native caption for now, because the system
  min/max/close buttons have to be measured from DWM before that row can be padded correctly
- **Monaco code editor**: double-click a source file to open a local Monaco editor with syntax
  highlighting, language auto-detection, Ctrl+S save, an encoding-preserving writer, a dirty
  marker and a cursor readout. All assets are bundled, so editing works offline. Like document
  mode there is no "Code" button in the tab strip: the editor appears because you opened a file
- **Built-in Office suite**: double-click a `.docx` / `.xlsx` / `.pptx` and the file manager gets
  out of the way - tab strip, navigation toolbar, command bar and status bar collapse, the document
  takes the window, and the sidebar stays as a file browser. There is no separate "Office" button
  to press; document mode is a consequence of opening a document, and Back (or Ctrl+W) returns to
  the folder. Three real editors are included:
  - **Word**: page canvas, styles, font/size/colour, bold / italic / underline, alignment,
    bullets/numbering, tables, inline pictures, hyperlinks, find/replace, undo/redo and HTML export.
  - **Excel**: worksheet tabs, a scrollable cell grid, formula bar with a formula engine
    (`SUM`, `AVERAGE`, `IF`, cell references and ranges), cell styles, row/column editing and
    live status aggregates.
  - **PowerPoint**: slide thumbnails, an editable 16:9 canvas with text boxes, add/duplicate/
    delete/reorder slides, layouts and speaker notes.
  - **Advanced OOXML panel**: Word multi-level numbering definitions (decimal, letters, Roman,
    bullets, start values, restart rules, legal style), tracked changes (w:ins / w:del /
    w:rPrChange) with accept / reject, Excel charts (column, bar, line, pie, area) rendered natively,
    pivot tables with Sum / Count / Average / Min / Max aggregation, embedded OLE objects that can
    be extracted, VBA module source inspection, and FileHub-native macro record / replay.
  - **WPS native formats**: opens Kingsoft WPS Writer `.wps` / `.wpt`, Spreadsheet `.et` / `.ett`
    and Presentation `.dps` / `.dpt`. Three containers are supported: OOXML packages saved with a
    WPS extension, legacy OLE/BIFF/WPS binaries (text, shared strings, cell records and slide text
    are decoded), and RTF-based Writer documents. A decoded document is edited as a temporary
    OOXML copy and Save As produces a `.docx` / `.xlsx` / `.pptx` file; the original WPS file is
    never overwritten with OOXML bytes.
  Ctrl+S writes changes back to the original file. Powered by the Open XML SDK - no Office
  installation, no COM, cross-platform. VBA code is never executed; macro recording replays
  FileHub editor actions only. Advanced OOXML features outside our implementation (VBA execution,
  macros with dialogs, pivot charts, OLE activation) are documented in THIRD-PARTY-NOTICES.md
- **Settings dialog**: the gear in the tab strip (or Settings in the sidebar footer, or `Ctrl+,`)
  opens a modal settings window laid out like VS Code's: a rail on the left, grouped rows on the
  right, no OK/Apply step. Six sections - **General** (language, theme, sidebar, details pane, dual
  pane), **File manager** (hidden files, show extensions, folders first, thumbnails, icon view,
  default sort column and direction, delete confirmation), **Code editor** (font family and size,
  tab size, insert spaces, word wrap, minimap, line numbers, whitespace rendering, bracket pair
  colorization, cursor style, smooth scrolling, auto save delay, editor theme),
  **Office** (default document type, body font size, default zoom, ruler, wide page, cell grid
  lines, track changes by default, auto save + interval, macro security), **Extensions** and
  **About** (version, build channel, licence, third-party notices). "Restore defaults" resets
  everything. Code values are pushed into Monaco through `fileHub.applyOptions`, Office values
  re-render the open document, and preferences persist to
  `%APPDATA%/FileHub/preferences.json` (macOS: `~/Library/Application Support/FileHub/`)
- **VS Code extensions manager**: a section of the settings dialog. It discovers extensions
  installed under `.vscode`, `.vscode-insiders`, `.vscode-oss`, `.cursor` and `.windsurf`, lets you
  enable or disable them for FileHub, and shows their language / theme / snippet / grammar
  contributions. Enabled extensions supply file associations, colour themes and completion
  snippets to Monaco. Extension JavaScript is **not** executed. Manifests that store their title
  as a `%key%` placeholder are resolved against `package.nls(.locale).json`
- **Visual Studio Marketplace tab**: searches Microsoft's public gallery, shows version /
  publisher / rating / install count, downloads the VSIX on request and installs it into
  FileHub's own extension folder (never into VS Code's managed metadata). Uninstall removes only
  packages FileHub installed. Listings and licence links are surfaced in the details pane.
  FileHub is not affiliated with Microsoft; the Gallery API has no public SLA. No extension
  package is bundled with FileHub, and search terms are sent to Microsoft
- **Context menus that know what was clicked**: a file or folder gets Open, Open with (code
  editor / Office / quick look / system default), Open in new tab, cut / copy / paste, rename or
  batch rename, delete, compress, extract (archives only), copy path / name, reveal and "open in
  terminal"; empty space gets New folder, paste, refresh, Sort by and View submenus with a tick on
  the active entry, show hidden files, select all, terminal and settings. Items carry icons and
  shortcut hints, and right-clicking a multi-selection keeps the whole selection
- **Delete always moves items to the recycle bin** (Windows Shell, macOS `~/.Trash`, Linux
  `gio trash`). Nothing is silently deleted permanently
- **Every location is a virtual path**: the local disk, an archive, a NAS share and an S3 bucket
  all flow through the same `IFileSystemProvider` contract, so navigation, breadcrumbs, search,
  drag and drop and file operations behave identically everywhere
- **Archives open as folders**: double-click a `.zip`, `.tar`, `.tar.gz`, `.bz2`, `.xz`, `.7z` or
  `.rar` to browse it, extract a subfolder, or drop files into the current folder. Right-click
  compresses the selection into a zip; extraction never overwrites, it adds " (n)" suffixes.
  Encrypted zips ask for a password through `ArchiveFileSystemProvider.PasswordProvider`
- **Transfer queue with real progress**: copy and move run on a background queue showing bytes
  done, current item and a per-job progress bar. Jobs can be paused or cancelled mid-file, and a
  target that already exists raises a conflict prompt (skip / keep both / replace / cancel) with
  an "apply to this and all remaining items" option
- **Undo and redo**: renames (single and batch) and new-folder creation are recorded on an undo
  stack (100 entries), so a wrong batch rename is one Ctrl+Z away. Transfers are deliberately not
  on the stack yet: the queue resolves name collisions while copying, so the final paths are only
  known afterwards
- **Batch rename**: pattern tokens (`{name}` `{ext}` `{orig}` `{n}` `{n:000}` `{date}` `{time}`
  `{modified}` `{exif:date}` `{exif:model}`), pipe transforms (`{name|upper}` `{name|slug}`), a
  regular-expression find/replace pass, prefix/suffix, case mode, and a live preview that flags
  collisions and illegal names before anything is renamed
- **Quick look**: press Space for a full-window preview of the focused item without leaving the
  folder. Arrow keys walk the folder, Space or Esc closes it. Images, text, Markdown, PDF info,
  audio/video metadata, fonts, archives, Office properties and EXIF all resolve here
- **Dual pane**: a second independent folder view beside the first (own path, history, selection),
  which makes drag-to-move between two trees trivial
- **Command palette**: `Ctrl+Shift+P` lists every verb in the window with keywords, filtered as
  you type; plugin commands are appended to the same list
- **Paste a URL into the address bar**: `https://`, `http://` and bare `www.` hosts are resolved
  for you. A directory - either a WebDAV collection or a plain HTML index - opens as a folder you
  can navigate, preview and copy from; a single file is pulled into the per-user web cache and
  opened with the matching viewer; an ordinary web page is handed to the system browser instead of
  showing an empty folder. `user:pass@host` in the URL becomes Basic credentials, and the hosts you
  opened come back under Quick access
- **Network locations**: add SMB, SFTP, WebDAV or S3-compatible endpoints from the palette. The
  bookmark is stored in `remote-locations.json`, the password in the OS keychain where available,
  and the location appears in the sidebar like a drive
- **Plugins**: drop a folder with `plugin.json` and an assembly into
  `%APPDATA%/FileHub/plugins`. Each plugin loads in its own collectible `AssemblyLoadContext` and
  may register commands, list columns, preview handlers and whole file-system providers. A plugin
  that throws is marked failed; the host keeps running. See `docs/plugins.md`
- **Folder rules**: `watch-rules.json` describes "when a file matching X lands in Y, move / copy /
  rename it to Z", applied by a background watcher through the same VFS layer
- **Command line and links**: `filehub <path>` opens that folder, and
  `filehub://open?path=<url-encoded>` does the same from the OS
- Multiple tabs, each with its own path, history, selection and sidebar location. Tabs can be
  reordered by dragging, closed with the middle mouse button, duplicated, and the open folders
  are restored on the next launch. Right-click a tab for close-other / close-to-the-right
- Multi-select, hidden items toggle, light / dark / system theme, status bar statistics
- Full keyboard shortcuts

| Shortcut | Action | Shortcut | Action |
| --- | --- | --- | --- |
| `Alt+Left` / `Alt+Right` | Back / Forward | `F2` | Rename |
| `Backspace` | Up one level | `F5` | Refresh |
| `Ctrl/Cmd+C` / `X` / `V` | Copy / Cut / Paste | `Delete` | Move to recycle bin |
| `Ctrl/Cmd+A` | Select all | `Enter` | Open selection |
| `Ctrl/Cmd+F` | Focus search | `Ctrl/Cmd+L` | Edit path |
| `Esc` | Clear search / leave path editing | Double-click | Open |
| `Ctrl/Cmd+T` | New tab | `Ctrl/Cmd+W` | Close tab |
| `Ctrl/Cmd+Tab` / `Ctrl/Cmd+Shift+Tab` | Next / previous tab | `Ctrl/Cmd+1`..`8` | Jump to tab |
| `Ctrl/Cmd+Shift+T` | Reopen closed tab | `Ctrl/Cmd+Shift+D` | Duplicate tab |
| `Space` | Quick look | `Ctrl/Cmd+Shift+P` | Command palette |
| `Ctrl/Cmd+Z` | Undo | `Ctrl/Cmd+Shift+Z` | Redo |
| `Ctrl/Cmd+Shift+Left` / `Right` | Move tab left / right | Right-click tab | Tab context menu |

## Build and run

Everything restores without configuration: the `CodeHub-FluentUI` nupkg is committed in
`local-feed/` and `NuGet.Config` points at it with a relative path. No machine-specific path is
needed after cloning.

```bash
./build.sh
./build.sh -c Release          # any dotnet build arguments are passed through
dotnet run --project src/FileHub.Desktop/FileHub.Desktop.csproj
```

`build.sh` applies resource guards (`nice -n 20`, `-m:2`, `-nodeReuse:false`) and picks an SDK in
this order:

1. `FILEHUB_DOTNET` environment variable
2. `$HOME/dotnet11x64/dotnet` (if present)
3. `dotnet` on `PATH`

On a normal machine, the official .NET 10 SDK is enough. The two environment issues below are
specific to the author's machine and explain the extra MSBuild target.

### Why `FileHubCompileAvaloniaXaml` exists

The CodeNet **arm64** .NET 10.0.400 SDK comments out
`CallTarget Targets="$(TargetsTriggeredByCompilation)"` in
`Microsoft.CSharp.CurrentVersion.targets`, which is the hook that runs Avalonia 12's
`CompileAvaloniaXaml`. The result is a build with 0 errors whose output still contains raw XAML,
so the app fails at runtime with `No precompiled XAML found for FileHub.Desktop.App`.

`src/FileHub.Desktop/FileHub.Desktop.csproj` calls the target explicitly with
`AfterTargets="CoreCompile"`, so XAML is compiled with any SDK. Avalonia 12.1 enables compiled
bindings by default; this project uses reflection bindings, so it sets
`AvaloniaUseCompiledBindingsByDefault=false`. MSBuild runs a target only once per build, so the
official SDK (which has the hook) does not compile XAML twice; CI uses the official SDK safely.

Known environment limitation: the **arm64 CodeNet SDK SIGSEGVs (139)** inside that XAML compiler
task. The official arm64 SDK or the x64 SDK both work; the x64 SDK was measured at about 7 s with
0 warnings and 0 errors.

### Offline feed

`CodeHub-FluentUI 3.1.0-codehub.1` comes from
[`Hub-code-develop/CodeHub-FluentUI`](https://github.com/Hub-code-develop/CodeHub-FluentUI)
(MIT) and is not on nuget.org. Its 1.1 MB nupkg is committed so the repository is self-contained.
See `local-feed/README.md` for upgrade instructions. The package requires Avalonia 12.1.0 /
net10.0, so do not change those versions independently.

## GitHub Actions

`.github/workflows/build.yml` runs on pushes to `main`, pull requests and manual dispatch:

| Runner | RID | Steps |
| --- | --- | --- |
| `ubuntu-latest` | `linux-x64` | restore, Release build, RID restore, publish, XAML gate, upload artifact |
| `windows-latest` | `win-x64` | same |
| `macos-latest` | `osx-arm64` | same |

- `actions/setup-dotnet@v4` installs the SDK from `global.json` (10.0.400); no secrets needed
- `dotnet restore --configfile NuGet.Config` uses the bundled `local-feed/`; no private feed or token
- Solution-level `dotnet build` rejects `-r` (NETSDK1134), so the RID is only applied to the
  single-project publish. A framework-dependent RID publish is about **31 MB**; without a RID
  it flattens to 564 MB, mostly Windows `.pdb` files
- **XAML gate**: `publish/FileHub.dll` must contain `!XamlIlPopulate`, which catches the
  "0 errors but no precompiled XAML" failure mode
- Each platform uploads a `FileHub-<rid>-net10.0` artifact (retained for 14 days)

### Release channels

| Channel | Workflow | Trigger | Tag | Release |
| --- | --- | --- | --- | --- |
| Stable | `publish-release.yml` | manual dispatch (optional `version`, default = bump patch) | `vX.Y.Z` (created by the workflow) | normal |
| Nightly | `publish-nightly.yml` | daily 20:00 UTC (04:00 Shanghai) + manual | `publish-nightly` | prerelease, updated in place |
| Preview | `publish-preview.yml` | every push to `main` + manual | `preview` | prerelease, updated in place |

All three call the shared `build-artifacts.yml` reusable workflow, which builds `win-x64`,
`linux-x64`, `osx-arm64` and `osx-x64`, applies the same XAML gate, and packages:

| Platform | Asset |
| --- | --- |
| Windows x64 | `FileHub.win.x64.<version>.zip` |
| Linux x64 | `FileHub.linux.x64.<version>.tar.gz` |
| macOS Apple Silicon | `FileHub.osx.arm64.<version>.dmg` (+ `.app.zip`) |
| macOS Intel | `FileHub.osx.x64.<version>.dmg` (+ `.app.zip`) |

- Stable versions are plain SemVer; nightly/preview use `0.<days>.<hours>.<minutes>` counted
  from 2026-11-15 in Asia/Shanghai time, so each build gets a unique, monotonically increasing
  version even for the same commit.
- The version, channel, commit and build time are embedded in the binary as a resource
  (`Assets/version-ci.json`) and shown next to the sidebar's Settings entry on hover. Local
  builds show `local-build`; no configuration is needed.
- Nightly and preview keep a single release each. Assets are replaced in place - never delete
  and recreate the tag, because that leaves a window where the release URL returns 404.
- Scheduled nightly runs with no commits in the previous 24 hours are skipped rather than
  cancelled, so the Actions history stays green.
- Packages are **framework-dependent**: install the .NET 10 Runtime first. This avoids shipping
  a self-contained macOS CoreCLR that would hit the macOS 27 EXC_GUARD launch failure. macOS
  builds are ad-hoc signed (not notarized); on first launch use right-click -> Open.
- Release notes are generated from `git log` against the latest stable tag; PR builds remain
  covered by `build.yml`.

## Structure

```text
FileHub/
├── .github/workflows/     build.yml (three-platform CI)
├── FileHub.slnx           solution
├── global.json            SDK 10.0.400
├── NuGet.Config           nuget.org + bundled offline feed
├── local-feed/            CodeHub-FluentUI nupkg (MIT)
├── licenses/Monaco/       monaco-editor LICENSE + ThirdPartyNotices.txt (MIT)
├── LICENSE                MIT
├── build.sh               build script with resource guards
├── README.md              this file (English, primary)
├── README.zh-CN.md        Chinese README
└── src/
    ├── FileHub.Files/     logic only, no UI dependency, unit-testable
    │   ├── Models/        FileEntry / LocationEntry
    │   ├── Plugin/        IFileHubPlugin contracts (commands, columns, previewers, providers)
    │   ├── Vfs/           VfsPath / IFileSystemProvider / local + archive + http(s) + SMB + SFTP + WebDAV + S3
    │   └── Services/      FileSystemService / ArchiveService / TransferQueue / UndoService /
    │                      BatchRenameService / FileInspector / WebLocationService / WebCacheService /
    │                      WebHistoryStore / WatchRuleService / RecycleBinService /
    │                      PlatformService / FileClipboard / PreviewService / DirectoryWatcherService
    ├── FileHub.Code/      logic only, code editing + VS Code contribution data
    │   ├── Models/        ExtensionInfo / ExtensionLanguage / ExtensionTheme / ExtensionSnippet
    │   └── Services/      CodeFileService / CodeLanguageService / ExtensionDiscoveryService / MonacoThemeConverter
    ├── FileHub.Office/    logic only, Office documents (Open XML SDK 3.3.0, MIT)
    │   ├── Models/        OfficeDocument / WordParagraph / ExcelSheet / PowerPointSlide
    │   └── Services/      OfficeDocumentService (load / save / create blank documents)
    └── FileHub.Desktop/   Avalonia application
        ├── App.axaml      theme entry point
        ├── Assets/Monaco/ bundled monaco-editor 0.52.2 min/vs assets (MIT)
        ├── Styles/        application styles
        ├── ViewModels/    ShellViewModel (tabs + pages) / MainViewModel / CodeEditorViewModel / ExtensionsViewModel / OfficeViewModel
        ├── Views/         MainWindow / SettingsDialog (modal settings) / Views/Controls (Files App layout ports + editor + Office ribbon)
        └── Services/      LocalizationService / DialogService / ThumbnailService / DragDropPayload /
                           MonacoAssetServer / FileInspectionService / PluginHost
```

## Verification

- `build.sh`: 0 warnings, 0 errors; XAML compiled into `FileHub.dll` (`!XamlIlPopulate` present)
- Headless (no GUI window) verification: `App.axaml` + `FluentAvaloniaTheme` +
  `avares://FileHub/Styles/AppStyles.axaml` load, every MainWindow control instantiates,
  home directory enumerates 55 items / 7 locations / 3 breadcrumb segments, filtering
  55 -> 0 -> 55 works
- Language detection verified under four environment configurations (unset, `auto`, `en`,
  `zh-CN`), including the three-state in-app language cycle
- Preview verification (headless Skia): Pictures folder decodes 7/7 image thumbnails; selecting
  an image produces a 512px pane bitmap in both Details and Preview tabs; selecting `README.md`
  produces a 4004-character text preview starting with `# FileHub`
- Drag-and-drop verification (headless): move 2 files into a folder, copy back with the source
  kept, folder-into-itself rejected, name collisions renamed not overwritten, payload round-trip
- Watcher verification: 12 burst writes produce one coalesced callback; end-to-end 3 external
  creates grow the list 1 -> 4 with selection preserved, external delete refreshes too
- Monaco / extension verification: 30 installed extensions scanned (23 language contributions,
  2 themes, 5 snippet sets, 22 file associations); a real VS Code theme converts to 195 Monaco
  rules; the local asset server serves `index.html` and `loader.js` and returns 404 for path
  traversal; UTF-8 BOM text round-trips; a browser smoke test confirms Monaco initialises with
  C# syntax highlighting
- A fresh clone from GitHub restores and builds with no manual setup

## Known limitations

- FileHub is **not** a VS Code extension host: extension JavaScript, language servers, debuggers
  and contributed UI are not run. Only declarative language / theme / snippet contributions are read
- TextMate grammars are not executed. Highlighting comes from Monaco's bundled language set; a
  custom language id contributed by an extension is registered but renders as plain text
- Some virtual / network volumes cannot raise change events; F5 manual refresh still works there
- Recursive search has a fixed 300 item limit to keep large trees under control
- Web locations follow what the server exposes: listings need PROPFIND or an HTML index, and
  writing needs WebDAV (PUT/MKCOL/DELETE/MOVE). Uploading to a plain static server is refused with
  the server's own status rather than a silent failure. Only Basic authentication is supported, and
  the site's cookies are kept in memory for the session, never written to disk
- Network providers are implemented but have not been exercised against real hardware in CI:
  SMB uses SMBLibrary (LGPL-3.0, dynamically linked — see THIRD-PARTY-NOTICES.md), SFTP uses
  SSH.NET without `known_hosts` verification, WebDAV assumes a standards-compliant PROPFIND, and
  S3 assumes path-style addressing. Treat them as beta and report what breaks
- Archives are read-only in place. Editing a file inside a zip means extracting it first; the
  provider supports extraction and re-compression through the context menu, not in-place writes
  inside the container. RAR is read-only, and creating a **password-protected** archive is not
  supported: SharpCompress can read encrypted entries but its writers have no password option, so
  FileHub refuses rather than silently producing an unencrypted archive
- Compressed tars (`.tar.gz`, `.tar.bz2`, `.tar.xz`) must be decompressed once to list them;
  the snapshot is cached afterwards, so the first open of a very large one is slow
- Quick look reads PDF page counts by scanning objects rather than following every cross-reference
  stream, so an unusual or linearised PDF can report an approximate count
- The plugin API is early: contracts may change between minor versions, and there is no sandbox
  beyond per-plugin assembly isolation — a plugin runs with the app's own permissions
- Dual pane shares one window width; there is no tab-per-pane yet
- Thumbnails cover common bitmap formats via Avalonia's Skia decoder; SVG, HEIC and video
  thumbnails need platform codecs and currently use the category icon instead
- Copy / move uses an in-app clipboard; only "copy path" writes to the system clipboard
- Chinese variants (Traditional, Cantonese, ...) currently share the Simplified Chinese strings

## License

[MIT](LICENSE) - Copyright (c) 2026 Hub-code-develop. FileHub is a derived work: its
file-browsing UI and command structure were ported from Files App and WinUI 3 (both MIT), and
it bundles monaco-editor (MIT). When redistributing FileHub, in source or binary form, include
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) alongside `LICENSE`; the upstream copyright
and permission notices are reproduced there verbatim as the MIT License requires.
