# FileHub

[![Build](https://github.com/Hub-code-develop/FileHub/actions/workflows/build.yml/badge.svg)](https://github.com/Hub-code-develop/FileHub/actions/workflows/build.yml)

[English README (primary)](README.md)

用 **.NET 10 + Avalonia 12 + FluentUI-CodeHub（`CodeHub-FluentUI`）** 写的跨平台文件管理器，
界面**移植自** Windows 上的 [Files App](https://github.com/files-community/Files)（WinUI 3 文件管理器）。

主说明文件是英文的 [README.md](README.md)，本文件是中文翻译。
**文档默认英文；应用界面不会默认英文**，详见[界面语言](#界面语言)。

![FileHub 主界面](docs/filehub-main.png)

## 版权与来源声明：移植自 Files 的代码

**FileHub 的界面是把 [Files](https://github.com/files-community/Files) 的 XAML 布局移植过来的，
不只是"模仿"。** 它把该仓库的布局结构取了过来，再用 Avalonia 控件重新表达。

| Files 源文件（MIT） | 移植到 FileHub 的位置 |
| --- | --- |
| `src/Files.App/Views/MainPage.xaml` | `src/FileHub.Desktop/Views/MainWindow.axaml` 根 Grid |
| `src/Files.App.Controls/Sidebar/SidebarView.xaml` | `Views/Controls/FilesSidebar.axaml(.cs)`（头部 / 菜单项 / 页脚三行） |
| `src/Files.App/UserControls/NavigationToolbar.xaml` | `Views/Controls/FilesNavigationToolbar.axaml(.cs)`（高 48，按钮 36x32） |
| `src/Files.App/UserControls/Toolbar.xaml` | `Views/Controls/FilesToolbar.axaml(.cs)`（8px 卡片，按钮由命令集动态生成） |
| `src/Files.App/UserControls/TabBar/TabBar.xaml` | `WinTabStrip` 标签栏（标签高 30，右侧窗口操作区） |
| `src/Files.App/UserControls/StatusBar.xaml` | `Views/Controls/FilesStatusBar.axaml(.cs)`（高 32，1x18 分隔线） |
| `src/Files.App/UserControls/Pane/InfoPane.xaml` | `Views/Controls/FilesInfoPane.axaml(.cs)`（宽 300，标签胶囊） |
| `src/Files.App/Views/Layouts/DetailsLayoutPage.xaml` | `Views/Controls/DetailsLayoutPage.axaml(.cs)`（列头高 40，34/*/110/150/160 列宽） |
| `src/Files.App/Actions/IAction.cs`、`BaseUIAction.cs`、`Data/Commands/Manager/CommandManager.cs` | `src/FileHub.Desktop/Actions/`（`CommandCodes`、`IRichCommand`、`RichCommand`、`CommandManager`） |
| `src/Files.App/Styles/TabBarStyles.xaml` | `src/FileHub.Desktop/Styles/FileHubTheme.axaml` |

移植过来的内容：整体骨架（顶部标签栏、侧栏分组含快速访问与驱动器、导航工具栏、命令栏、
文件区加可开关的详细信息窗格、状态栏）、命令栏分组（布局 / 排序 / 详细信息 / 隐藏项目），
以及具体的布局数值（行高、列宽、圆角、内边距、间距）。

没有移植的内容：WinUI 3 的 XAML 无法在 Avalonia 上运行，所以每个控件都映射到了
CodeHub-FluentUI 或 Avalonia 的等价控件，Files 专属的基础设施（Shell COM、`x:Bind`、
`ThemeResource`、WinRT 存储、`ItemsRepeater`）换成了 FileHub 的 ViewModel 与跨平台
`FileSystemService`。Files 的命令体依赖 WinRT/Win32，无法在 macOS/Linux 上运行；
我们移植的是命令的**结构**（标识符枚举、注册表、携带标签/图标/快捷键/可执行状态的命令对象），
命令体改为调用 FileHub 的跨平台服务。

**Files 采用 MIT 许可证，允许再分发（redistribution）。由于移植了它的布局与命令结构代码，
其原始版权声明与许可声明已完整保留在
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 中。** 再分发 FileHub（源码或二进制）时，
必须随附该文件与 `LICENSE`。该文件同时列出全部其它第三方组件。

FileHub 本身采用 [MIT 许可证](LICENSE)。感谢 Files 社区做出了一个设计确实出色的文件管理器。

`CodeHub-FluentUI` 是来自
[`Hub-code-develop/CodeHub-FluentUI`](https://github.com/Hub-code-develop/CodeHub-FluentUI)
的独立依赖（MIT），离线包说明见 [local-feed/README.md](local-feed/README.md)。

## 控件层：从 WinUI 3 源码移植

`src/WinUI3.Fluent/` 是 **直接照 WinUI 3 默认控件样式源码移植**的（
[microsoft/microsoft-ui-xaml](https://github.com/microsoft/microsoft-ui-xaml)，MIT）。
WinUI 3 的样式是明文 XAML，所以颜色、圆角、内边距、各状态配色都是照抄
`controls/dev/CommonStyles/*_themeresources.xaml`，不是照截图用眼睛调出来的。

| 控件 | 状态 |
| --- | --- |
| Button、ToggleButton、CheckBox、RadioButton、ToggleSwitch、TextBox、TextBlock | 已移植 |
| ListBoxItem / ListViewItem | 已移植 |
| ToolTip、FlyoutPresenter | 已移植 |
| TabView 标签栏（含关闭按钮、中键关闭、拖拽重排、右键菜单、溢出滚动） | 已移植（`WinTabStrip`） |
| AppBarButton（命令栏按钮） | 已移植（`WinAppBarButton`） |
| ScrollBar | 沿用库自带实现 |
| NavigationView、BreadcrumbBar、InfoBar、ProgressRing、MenuFlyout、ComboBox | 由 CodeHub-FluentUI 提供 |

`ScrollBar` 是唯一刻意不移植的：Avalonia 的 `ScrollBar` 在 `OnApplyTemplate` 里接管
`PART_Track`，滑块比例来自 `ViewportSize`，照搬 WinUI 模板会让滑块退化成一个小圆点。
宁可用能滚动的原生滚动条，也不交付一个长得像但坏掉的。

无法做到一致的三处：Acrylic / Mica（Windows 合成器背景，无可移植对应物）、
Segoe UI Variable（无再分发授权）、Composition 弹簧动画（用插值过渡近似）。
逐文件映射表在 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 界面语言

**应用在启动时自动检测操作系统语言**，界面不把英文写死成默认值：中文系统显示中文，
英文系统显示英文。检测顺序（取第一个有效信号）：

| 平台 | 信号 |
| --- | --- |
| macOS | `defaults read -g AppleLanguages`，其次 `AppleLocale` |
| Windows | `CultureInfo.InstalledUICulture` |
| Linux / Unix | `LC_ALL`、`LC_MESSAGES`、`LANG`（`C` / `POSIX` / `C.UTF-8` 不含语言信息，跳过） |
| 通用兜底 | `CultureInfo.CurrentUICulture` |

macOS 优先读系统语言偏好是故意的：从 Finder 启动的 GUI 应用不继承任何 `LC_*` 变量，
只有读 `AppleLanguages` 才能拿到用户真正的语言。语言按钮的提示里会显示实际用的信号，
例如 `zh-Hans-CN / macOS AppleLanguages`。

- `FILEHUB_LANG=en` / `zh-CN` / `auto` 可单次运行覆盖检测结果，`auto` 表示恢复跟随系统
- 界面语言按钮依次切换 **自动 / English / 中文 / 自动**。手动选择只在本次运行有效，
  下次启动仍跟随系统
- 目前提供英文与简体中文两套文案。其它语言回退到英文，仅仅是因为尚无对应翻译，
  不是因为默认偏好英文
- 只有文档默认英文。应用里每一条用户可见文案都在
  `Services/LocalizationService.cs` 中同时有英文和中文值

## 技术栈

| 组件 | 版本 / 来源 |
| --- | --- |
| .NET | `net10.0`（SDK 10.0.400） |
| Avalonia | 12.1.0 |
| UI 样式库 | `CodeHub-FluentUI` `3.1.0-codehub.1`（仓库自带离线源 `local-feed/`） |
| 主题 | `FluentAvaloniaTheme`（`FluentAvalonia.Styling`） |

用到的 FluentUI-CodeHub 控件：`FANavigationView`（侧栏）、`FABreadcrumbBar`（路径）、
`FAContentDialog`（新建 / 重命名 / 删除确认 / 属性）、`FAInfoBar`（操作反馈）、
`FAProgressRing` + `FASkeleton`（加载态，后者是 CodeHub 分支特有控件）、
`FAMenuFlyout` / `FAMenuFlyoutItem`（右键菜单）、`FASymbolIcon` / `FASymbolIconSource`（图标）。

## 功能

- 侧栏按平台自动列出主目录 / 桌面 / 下载 / 文档 / 图片 / 视频，以及当前已挂载卷
- 前进 / 后退 / 上一级 / 刷新，带导航历史
- 面包屑路径 + 可编辑地址栏（支持 `~`）
- 详细信息视图（名称 / 大小 / 类型 / 修改时间，点列头排序）与图标网格视图切换
- **图片缩略图与文件预览**：JPG / PNG / GIF / BMP / WebP / TIFF 在两种视图里都显示真实缩略图；
  选中文件后详情面板会显示大图预览或文本开头。面板的 **详细信息 / 预览** 两个标签可在属性与预览间切换。
  不支持的格式回退到分类图标。
- 即时筛选 + 递归搜索（结果上限 300，可取消）
- 文件操作：打开、在系统文件管理器中显示、新建文件夹、重命名、复制 / 剪切 / 粘贴、复制路径、属性
- **拖拽**：把选中的项目拖到文件夹行、面包屑片段或侧栏位置即可移动；按住 Ctrl 改为复制。
  从 Finder / 资源管理器拖入的文件按复制导入。重名自动加序号，绝不覆盖。
- **实时目录监视**：当前目录里外部新建 / 删除 / 重命名会自动刷新列表（合并事件、280 毫秒防抖），
  并保留当前选择。无法监视的卷仍可用 F5 手动刷新。
- **无系统标题栏，标签栏就是标题栏**：所有平台都去掉原生标题栏，客户区一直顶到窗口边缘，
  标签栏承担标题栏的职责 —— 拖动它移动窗口、双击缩放、滚轮在标签上横向翻页。
  各平台仍画在客户区上方的东西，这一行都会让出位置：macOS 左侧留给红绿灯，
  Windows 右侧留给最小化/最大化/关闭（宽度从 DWM 量），Linux/X11 没有系统按钮，由这一行自绘。
- **Monaco 代码编辑器**：双击源代码文件即可打开本地 Monaco 编辑器，支持语法高亮、语言自动识别、
  Ctrl+S 保存、保留原编码写入、未保存标记与光标位置显示。所有资源都随应用打包，离线也能写代码。
  与文档模式一样，标签栏里没有「代码」按钮：编辑器只会因为你打开了文件而出现。
- **内置 Office 工作套件**：双击 `.docx` / `.xlsx` / `.pptx` 即可进入文档模式 —— 标签栏、导航栏、
  命令栏和状态栏自动收起，文档占据窗口，侧边栏保留为文件浏览器。没有单独的「Office」按钮需要按，
  打开文档就是进入文档模式的唯一入口；点「文件」或 Ctrl+W 回到原文件夹。内含三款真正可编辑的组件：
  - **Word**：页面画布、样式、字体/字号/颜色、粗体/斜体/下划线、对齐、项目符号与编号、表格、内嵌图片、
    超链接、查找替换、撤销/重做与 HTML 导出。
  - **Excel**：工作表标签、可滚动单元格网格、公式栏与公式引擎（`SUM`、`AVERAGE`、`IF`、单元格引用与区域）、
    单元格样式、行列增删与实时统计。
  - **PowerPoint**：幻灯片缩略图、可编辑的 16:9 画布与文本框、新建/复制/删除/排序幻灯片、版式与演讲者备注。
  - **高级 OOXML 面板**：Word 多级编号定义（数字/字母/罗马/项目符号、起始值、重启规则、法律编号）、
    修订记录（w:ins / w:del / w:rPrChange）与接受/拒绝、Excel 图表（柱形/条形/折线/饼图/面积图）原生预览、
    数据透视表（求和/计数/平均值/最值）、可提取的 OLE 嵌入对象、VBA 模块源码查看，以及 FileHub 原生宏录制/回放。
  - **WPS 自有格式**：可打开金山 WPS 文字 `.wps` / `.wpt`、表格 `.et` / `.ett`、演示 `.dps` / `.dpt`。
    支持三类容器：改扩展名的 OOXML 包、传统 OLE/BIFF/WPS 二进制（解码正文、共享字符串、单元格记录与
    幻灯片文本）、以及 RTF 形式的文字文档。解码后以临时 OOXML 副本编辑，另存为会生成 `.docx` / `.xlsx` /
    `.pptx`，**绝不会把 OOXML 字节覆盖回原 WPS 文件**。
  Ctrl+S 把修改写回原文件。底层是 Open XML SDK，无需安装 Office、不用 COM，跨平台可用。VBA 代码从不执行；
  宏录制只回放 FileHub 的编辑操作。未覆盖的高级特性（VBA 执行、带对话框的宏、数据透视图、OLE 激活等）
  详见 THIRD-PARTY-NOTICES.md。
- **设置弹窗**：点标签栏的齿轮（或侧栏「设置」、`Ctrl+,`）打开模态设置窗口，布局照 VS Code：
  左侧分区栏、右侧成组设置行，没有「确定 / 应用」。六个分区：
  **常规**（界面语言、主题、侧栏、详细信息窗格、双栏）、
  **文件管理器**（隐藏文件、显示扩展名、文件夹优先、缩略图、默认图标视图、默认排序列与升降序、
  删除前确认）、
  **代码编辑器**（字体族与字号、Tab 宽度、插入空格、自动换行、小地图、行号、空白字符、括号对着色、
  光标样式、平滑滚动、自动保存延迟、编辑器主题）、
  **Office**（默认文档类型、正文字号、默认缩放、标尺、宽页视图、单元格网格线、默认开启修订、
  自动保存与间隔、宏安全级别）、**扩展**、**关于**（版本、构建通道、许可、第三方声明）。
  「恢复默认值」一键还原；代码类选项经 `fileHub.applyOptions` 实时推给 Monaco，Office 选项会重绘
  已打开的文档；偏好持久化到 `%APPDATA%/FileHub/preferences.json`（macOS 为
  `~/Library/Application Support/FileHub/`）。
- **VS Code 扩展管理器**：设置弹窗中的一个分区。扫描 `.vscode`、`.vscode-insiders`、`.vscode-oss`、
  `.cursor`、`.windsurf` 下已安装的扩展，可为 FileHub 单独启用 / 禁用，并展示语言、主题、代码片段和
  语法贡献。启用的扩展会向 Monaco 提供文件关联、配色主题和补全片段；**不会执行扩展的 JavaScript**。
  标题写成 `%key%` 占位符的清单会用 `package.nls(.locale).json` 解析出真实名称。
- **Visual Studio Marketplace 官方商店标签**：搜索 Microsoft 公共 Gallery，展示版本 / 发布者 /
  评分 / 安装量，按需下载 VSIX 并安装到 FileHub 自己的扩展目录（不碰 VS Code 管理的元数据）。
  卸载只移除 FileHub 安装的包；详情面板可打开商店页面与许可证链接。FileHub 与 Microsoft
  无隶属关系，Gallery API 无公开 SLA；FileHub 不随包分发任何扩展，搜索词会发送给 Microsoft。
- **会看对象的右键菜单**：文件 / 文件夹有打开、打开方式（代码编辑器 / Office / 快速查看 / 系统默认）、
  在新标签页打开、剪切复制粘贴、重命名 / 批量重命名、删除、压缩、解压（仅压缩包）、复制路径 / 名称、
  在文件管理器中显示、在终端中打开；空白处有新建文件夹、粘贴、刷新、排序方式与视图子菜单（当前项打勾）、
  显示隐藏文件、全选、终端与设置。菜单项带图标与快捷键提示，多选时右键仍保留整个选择。
- **删除一律移到回收站**（Windows Shell / macOS `~/.Trash` / Linux `gio trash`），绝不静默永久删除
- **所有位置统一走虚拟路径**：本地磁盘、压缩包、NAS 共享、S3 桶都实现同一套
  `IFileSystemProvider` 契约，导航、面包屑、搜索、拖拽与文件操作在任何位置行为一致
- **压缩包当目录用**：双击 `.zip` / `.tar` / `.tar.gz` / `.bz2` / `.xz` / `.7z` / `.rar` 直接进入浏览，
  可以只解压某个子目录，也可以把文件拖进压缩包所在目录。右键把选中项压成 zip；解压同名文件
  一律加 " (n)" 后缀，绝不覆盖。加密 zip 通过 `ArchiveFileSystemProvider.PasswordProvider` 询问密码
- **传输队列**：复制 / 移动在后台队列里执行，显示已传字节、当前文件与逐项进度条，可暂停、可取消；
  目标已存在时弹出冲突选项（跳过 / 两个都留 / 替换 / 取消），可勾选"应用到其余所有项"
- **撤销 / 重做**：重命名、新建文件夹、传输都进撤销栈（100 条），批量改错名一个 Ctrl+Z 就能回退
- **批量重命名**：支持 `{name}` `{ext}` `{orig}` `{n}` `{n:000}` `{date}` `{time}` `{modified}`
  `{exif:date}` `{exif:model}` 占位符、`{name|upper}` 这类管道转换、正则查找替换、前后缀与大小写模式，
  并带实时预览，改名之前就能看到重名与非法字符
- **快速查看**：按空格在整窗浮层里预览当前项，不离开当前文件夹；方向键切换、空格或 Esc 关闭。
  图片、文本、Markdown、PDF 信息、音视频元数据、字体、压缩包、Office 属性与 EXIF 都在这里解析
- **双栏对照**：右侧再开一个独立文件夹视图（各自路径、历史、选择），两边直接拖拽即可搬文件
- **命令面板**：`Ctrl+Shift+P` 列出窗口内所有动作并支持关键词过滤，插件命令也接在同一列表
- **地址栏直接贴网址**：`https://`、`http://` 和裸 `www.` 开头都会自动解析。目录（WebDAV 集合或
  普通 HTML 索引页）会当成文件夹打开，可导航、预览、复制；单个文件会下载到用户级 web 缓存并用对应
  查看器打开；普通网页则交给系统浏览器，而不是显示一个空文件夹。URL 里的 `user:pass@host` 会转成
  Basic 认证，打开过的站点会回到"快速访问"
- **网络位置**：从命令面板添加 SMB / SFTP / WebDAV / S3 兼容位置；书签存进 `remote-locations.json`，
  密码优先交给系统钥匙串，位置会像驱动器一样出现在侧栏
- **插件**：把带 `plugin.json` 的文件夹放进 `%APPDATA%/FileHub/plugins` 即可。每个插件在独立的
  可回收 `AssemblyLoadContext` 里加载，可注册命令、列表列、预览器和完整的文件系统 Provider；
  插件初始化抛异常只会被标记失败，不会带崩主程序。写法见 `docs/plugins.md`
- **文件夹规则**：`watch-rules.json` 描述"匹配 X 的文件进入 Y 后，移动 / 复制 / 改名到 Z"，
  后台监视器通过同一套 VFS 执行
- **命令行与链接**：`filehub <路径>` 直接打开该目录，`filehub://open?path=<url 编码>` 同理
- 多标签页，每个标签有独立的路径、历史、选中项与侧栏位置；标签可拖拽重排、中键关闭、复制，
  下次启动会恢复上次打开的目录；右键标签可关闭其他 / 关闭右侧标签
- 多选、隐藏文件开关、浅色 / 深色 / 跟随系统主题、状态栏统计
- 快捷键齐全

| 快捷键 | 动作 | 快捷键 | 动作 |
| --- | --- | --- | --- |
| `Alt+Left` / `Alt+Right` | 后退 / 前进 | `F2` | 重命名 |
| `Backspace` | 上一级 | `F5` | 刷新 |
| `Ctrl/Cmd+C` / `X` / `V` | 复制 / 剪切 / 粘贴 | `Delete` | 移到回收站 |
| `Ctrl/Cmd+A` | 全选 | `Enter` | 打开选中项 |
| `Ctrl/Cmd+F` | 聚焦搜索 | `Ctrl/Cmd+L` | 编辑路径 |
| `Esc` | 清除搜索 / 退出路径编辑 | 双击 | 打开 |
| `Ctrl/Cmd+T` | 新建标签 | `Ctrl/Cmd+W` | 关闭标签 |
| `Ctrl/Cmd+Tab` / `Ctrl/Cmd+Shift+Tab` | 下一个 / 上一个标签 | `Ctrl/Cmd+1`..`8` | 跳转到第 N 个标签 |
| `Ctrl/Cmd+Shift+T` | 重开已关闭标签 | `Ctrl/Cmd+Shift+D` | 复制标签 |
| `Ctrl/Cmd+Shift+Left` / `Right` | 标签左移 / 右移 | 右键标签 | 标签上下文菜单 |

## 构建与运行

依赖全部可还原：`CodeHub-FluentUI` 的 nupkg 已放在 `local-feed/`，根目录 `NuGet.Config`
用相对路径引用它，clone 后无需配置任何本机路径。

```bash
./build.sh
./build.sh -c Release          # 透传任意 dotnet build 参数
dotnet run --project src/FileHub.Desktop/FileHub.Desktop.csproj
```

`build.sh` 带资源护栏（`nice -n 20`、`-m:2`、`-nodeReuse:false`），并按下述优先级挑 SDK：

1. 环境变量 `FILEHUB_DOTNET`
2. `$HOME/dotnet11x64/dotnet`（若存在）
3. PATH 里的 `dotnet`

普通机器上直接用官方 .NET 10 SDK 即可；下面两个环境级问题只出现在作者机器上，
也是项目里多写一个 MSBuild target 的原因。

### 为什么有 `FileHubCompileAvaloniaXaml` 这个 target

CodeNet **arm64** SDK（10.0.400）把 `Microsoft.CSharp.CurrentVersion.targets` 里
`CallTarget Targets="$(TargetsTriggeredByCompilation)"` 整段注释掉了，而 Avalonia 12 的
`CompileAvaloniaXaml` 正是靠这个钩子触发。后果是：构建显示 0 错误，产物里却只有原始 XAML，
运行时报 `No precompiled XAML found for FileHub.Desktop.App`。

`src/FileHub.Desktop/FileHub.Desktop.csproj` 里加了 `AfterTargets="CoreCompile"` 的显式调用，
换任何 SDK 都会编译 XAML。另外 Avalonia 12.1 默认开启编译绑定，本项目用反射绑定，
所以显式设了 `AvaloniaUseCompiledBindingsByDefault=false`。MSBuild 保证同一 Target 在一次
构建中只执行一次，官方 SDK 自带钩子也不会二次编译，CI 走官方 SDK 是安全的。

注意，已知环境限制：**arm64 CodeNet SDK 在执行 XAML 编译任务时会 SIGSEGV(139)**。
官方 arm64 SDK 或 x64 SDK 均可，作者机器上实测 x64 SDK 约 7 秒 / 0 警告 0 错误。

### 关于 `local-feed/`

`CodeHub-FluentUI 3.1.0-codehub.1` 源自
[`Hub-code-develop/CodeHub-FluentUI`](https://github.com/Hub-code-develop/CodeHub-FluentUI)（MIT），
未发布到 nuget.org。为了让仓库自包含，把 1.1 MB 的 nupkg 一并提交。
详见 `local-feed/README.md`；若该包日后上了 nuget.org，删掉 `NuGet.Config` 里的
`codehub-local` 与这个目录即可。包依赖 Avalonia 12.1.0 / net10.0，版本不要单独改。

## GitHub Actions

`.github/workflows/build.yml` 在 push 到 `main`、PR 和手动触发时跑三平台矩阵：

| Runner | RID | 流程 |
| --- | --- | --- |
| `ubuntu-latest` | `linux-x64` | 还原、Release 构建、RID 还原、publish、XAML 门禁、上传 artifact |
| `windows-latest` | `win-x64` | 同上 |
| `macos-latest` | `osx-arm64` | 同上 |

- SDK 由 `actions/setup-dotnet@v4` 按 `global.json`（10.0.400）安装，不需要任何密钥
- `dotnet restore --configfile NuGet.Config` 显式使用仓库自带的 `local-feed/`，不需要私有源或 token
- 解决方案级 `dotnet build` 不接受 `-r`（NETSDK1134），所以 RID 只加在单项目 `dotnet publish`；
  带 RID 的 framework-dependent publish 约 **31 MB**（不带 RID 会摊平成 564 MB，大半是 Windows 的 `.pdb`）
- **XAML 门禁**：检查 `publish/FileHub.dll` 必须含 `!XamlIlPopulate`，
  防止「构建 0 错误但 XAML 没预编译」再次溜过去
- 每个平台上传 `FileHub-<rid>-net10.0` artifact（保留 14 天）

### 发布通道

| 通道 | Workflow | 触发 | Tag | Release |
| --- | --- | --- | --- | --- |
| 正式版 | `publish-release.yml` | 手动触发（可填 `version`，留空则递增补丁号） | `vX.Y.Z`（由 workflow 创建） | 正式 |
| 夜间构建 | `publish-nightly.yml` | 每天 UTC 20:00（北京时间 04:00）+ 手动 | `publish-nightly` | 预览，原地覆盖 |
| 预览版 | `publish-preview.yml` | 每次 push 到 `main` + 手动 | `preview` | 预览，原地覆盖 |

三者都调用共享的 `build-artifacts.yml` reusable workflow：构建 `win-x64`、`linux-x64`、
`osx-arm64`、`osx-x64`，跑同一道 XAML 门禁，然后打包：

| 平台 | 资产 |
| --- | --- |
| Windows x64 | `FileHub.win.x64.<version>.zip` |
| Linux x64 | `FileHub.linux.x64.<version>.tar.gz` |
| macOS Apple Silicon | `FileHub.osx.arm64.<version>.dmg`（另有 `.app.zip`） |
| macOS Intel | `FileHub.osx.x64.<version>.dmg`（另有 `.app.zip`） |

- 正式版用标准 SemVer；夜间/预览版用 `0.<天>.<时>.<分>`，从 2026-11-15 起按北京时间计数，
  所以即使同一个 commit 构建两次，版本号也唯一且单调递增。
- 版本、通道、commit 与构建时间作为资源嵌入程序（`Assets/version-ci.json`），鼠标悬停侧栏
  「设置」下方即可看到。本地构建显示 `local-build`，无需额外配置。
- 夜间版和预览版各只保留一个 Release，资产原地替换——绝不能删掉 tag 再重建，否则在删除与
  重建之间 Release 链接会 404。
- 定时任务如果过去 24 小时没有新提交会直接跳过，而不是取消 run，保持 Actions 历史干净。
- 产物是 **framework-dependent**，需要先装 .NET 10 Runtime。这样可避免自带自包含 macOS
  CoreCLR 在 macOS 27 上触发 EXC_GUARD 启动失败。macOS 包只做 ad-hoc 签名（未公证），
  首次打开请右键 -> 打开。
- 发版说明由 `git log` 相对最近正式版 tag 自动生成；PR 构建仍由 `build.yml` 覆盖。

## 结构

```text
FileHub/
├── .github/workflows/     build.yml（三平台 CI）
├── FileHub.slnx           解决方案
├── global.json            SDK 10.0.400
├── NuGet.Config           nuget.org + 仓库内离线源
├── local-feed/            CodeHub-FluentUI 的 nupkg（MIT）
├── licenses/Monaco/       monaco-editor 的 LICENSE + ThirdPartyNotices.txt（MIT）
├── licenses/SMBLibrary/   LGPL-3.0 声明与源码获取方式
├── docs/plugins.md        插件开发说明
├── LICENSE                MIT
├── build.sh               带资源护栏的构建脚本
├── README.md              英文主说明
├── README.zh-CN.md        本文件（中文说明）
└── src/
    ├── FileHub.Files/     纯逻辑，无 UI 依赖，可单测
    │   ├── Models/        FileEntry / LocationEntry
    │   ├── Plugin/        IFileHubPlugin 契约（命令 / 列 / 预览器 / Provider）
    │   ├── Vfs/           VfsPath / IFileSystemProvider / 本地 + 压缩包 + http(s) + SMB + SFTP + WebDAV + S3
    │   └── Services/      FileSystemService / ArchiveService / TransferQueue / UndoService /
    │                      BatchRenameService / FileInspector / WebLocationService / WebCacheService /
    │                      WebHistoryStore / WatchRuleService / RecycleBinService /
    │                      PlatformService / FileClipboard / PreviewService / DirectoryWatcherService
    ├── FileHub.Code/      纯逻辑，代码编辑与 VS Code 贡献数据
    │   ├── Models/        ExtensionInfo / ExtensionLanguage / ExtensionTheme / ExtensionSnippet
    │   └── Services/      CodeFileService / CodeLanguageService / ExtensionDiscoveryService / MonacoThemeConverter
    ├── FileHub.Office/    纯逻辑，Office 文档（Open XML SDK 3.3.0，MIT）
    │   ├── Models/        OfficeDocument / WordParagraph / ExcelSheet / PowerPointSlide
    │   └── Services/      OfficeDocumentService（读取 / 保存 / 新建空白文档）
    └── FileHub.Desktop/   Avalonia 应用
        ├── App.axaml      主题入口
        ├── Assets/Monaco/ 随包分发的 monaco-editor 0.52.2 min/vs 资源（MIT）
        ├── Styles/        应用样式
        ├── ViewModels/    ShellViewModel（标签与页面）/ MainViewModel / CodeEditorViewModel / ExtensionsViewModel / OfficeViewModel
        ├── Views/         MainWindow / SettingsDialog（设置弹窗）/ Views/Controls（Files 布局移植 + 编辑器 + Office 功能区）
        └── Services/      LocalizationService / DialogService / ThumbnailService / DragDropPayload /
                           MonacoAssetServer / FileInspectionService / PluginHost
```

## 验证情况

- `build.sh`：0 警告 0 错误，XAML 已编译进 `FileHub.dll`（可见 `!XamlIlPopulate`）
- 无窗口 headless 验证：`App.axaml` + `FluentAvaloniaTheme` +
  `avares://FileHub/Styles/AppStyles.axaml` 加载成功；`MainWindow.axaml` 全部控件实例化成功；
  主目录枚举 55 项 / 7 个位置 / 3 段面包屑；筛选 55 到 0 到 55 正常
- 语言检测在四种环境下验证（未设置、`auto`、`en`、`zh-CN`），含界面三态切换
- 拖拽验证（headless）：移动 2 个文件到目录、复制回来源且源文件保留、拒绝文件夹拖进自身、
  重名加序号不覆盖、payload 往返一致
- 监视验证：12 次连续写入合并为 1 次回调；端到端外部新建 3 个文件后列表 1 变 4 且选择保留，
  外部删除同样触发刷新
- Monaco / 扩展验证：扫描到 30 个已安装扩展（23 个语言贡献、2 个主题、5 组代码片段、22 个文件关联）；
  真实 VS Code 主题转换成 195 条 Monaco 规则；本地资源服务正确返回 index/loader.js 并对路径穿越返回 404；
  UTF-8 BOM 往返一致；浏览器实测 Monaco 初始化并完成 C# 语法高亮
- 从 GitHub 全新 clone 后可直接还原并构建，无需手工配置

## 已知限制 / 后续可加

- 压缩包原地只读：要改包里的文件得先解出来。右键可解包 / 重新打包，但不支持在容器内原地写入。
  rar 只读；**不能创建加密压缩包** —— SharpCompress 能读加密条目，但写入端没有密码参数，
  FileHub 会明确拒绝，而不是悄悄生成一个无密码的包
- 压缩型 tar（`.tar.gz` / `.tar.bz2` / `.tar.xz`）首次列举需要完整解压一遍，之后命中快照缓存；
  超大压缩包首次打开会慢

- FileHub **不是** VS Code 扩展宿主：不会执行扩展 JavaScript，也不运行语言服务器、调试器和扩展贡献的界面；
  只读取声明式的语言 / 主题 / 代码片段贡献
- 不执行 TextMate 语法文件。高亮来自 Monaco 自带语言集；扩展贡献的自定义语言 id 会注册，但按纯文本显示
- 网页位置的能力取决于服务器：列举需要 PROPFIND 或 HTML 索引页，写入需要 WebDAV
  （PUT/MKCOL/DELETE/MOVE）。向纯静态服务器上传会如实回报服务器状态码，不会静默成功。
  认证只支持 Basic，站点 Cookie 仅保存在会话内存里，不落盘
- 部分虚拟 / 网络卷无法产生变更事件，这些目录仍可用 F5 手动刷新
- 递归搜索固定 300 项上限，避免在大目录里失控
- 缩略图覆盖 Avalonia 的 Skia 解码器支持的常见位图格式；SVG、HEIC、视频缩略图需要平台编解码器，目前回退到分类图标
- 复制 / 移动为应用内剪贴板，不写系统剪贴板（只有复制路径会写）
- 繁体中文、粤语等中文变体目前共用简体中文文案

## 许可证

[MIT](LICENSE) - Copyright (c) 2026 Hub-code-develop。Files App 的完整声明（界面布局移植自该项目）、
随包分发的 monaco-editor（MIT）以及全部第三方组件说明见
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
