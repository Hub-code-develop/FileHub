# FileHub

[![Build](https://github.com/Hub-code-develop/FileHub/actions/workflows/build.yml/badge.svg)](https://github.com/Hub-code-develop/FileHub/actions/workflows/build.yml)

一个用 **.NET 10 + Avalonia 12 + FluentUI-CodeHub（`CodeHub-FluentUI`）** 写的跨平台文件管理器。
项目名、程序集名、命名空间与仓库名统一为 FileHub。

## 技术栈

| 组件 | 版本 / 来源 |
| --- | --- |
| .NET | net10.0（SDK 10.0.400） |
| Avalonia | 12.1.0 |
| UI 样式库 | `CodeHub-FluentUI` `3.1.0-codehub.1`（仓库自带离线源 `local-feed/`） |
| 主题 | `FluentAvaloniaTheme`（`FluentAvalonia.Styling`） |

用到的 FluentUI-CodeHub 控件：`FANavigationView`（侧栏）、`FABreadcrumbBar`（路径）、`FAContentDialog`（新建/重命名/删除确认/属性）、`FAInfoBar`（操作反馈）、`FAProgressRing` + `FASkeleton`（加载态，后者是 CodeHub 分支特有控件）、`FAMenuFlyout` / `FAMenuFlyoutItem`（右键菜单）、`FASymbolIcon` / `FASymbolIconSource`（图标）。

## 功能

- 侧栏按平台自动列出主目录 / 桌面 / 下载 / 文档 / 图片 / 视频 + 当前已挂载卷
- 前进 / 后退 / 上一级 / 刷新，带导航历史
- 面包屑路径 + 可编辑地址栏（支持 `~`）
- 详细信息视图（名称/大小/类型/修改时间，点列头排序）与图标网格视图切换
- 即时筛选 + 递归搜索（结果上限 300，带取消）
- 文件操作：打开、在系统文件管理器中显示、新建文件夹、重命名、复制/剪切/粘贴、复制路径、属性
- **删除一律移到回收站**（Windows Shell / macOS `~/.Trash` / Linux `gio trash`），绝不静默永久删除
- 多选、隐藏文件开关、浅色/深色/跟随系统主题切换、状态栏统计
- 快捷键齐全（见下表）

| 快捷键 | 动作 | 快捷键 | 动作 |
| --- | --- | --- | --- |
| `Alt+←/→` | 后退 / 前进 | `F2` | 重命名 |
| `Backspace` | 上一级 | `F5` | 刷新 |
| `Ctrl/Cmd+C/X/V` | 复制 / 剪切 / 粘贴 | `Delete` | 移到回收站 |
| `Ctrl/Cmd+A` | 全选 | `Enter` | 打开选中项 |
| `Ctrl/Cmd+F` | 聚焦搜索 | `Ctrl/Cmd+L` | 编辑路径 |
| `Esc` | 清除搜索 / 退出路径编辑 | 双击 | 打开 |

## 构建与运行

依赖全部可还原：`CodeHub-FluentUI` 的 nupkg 已放在 `local-feed/`，
根目录 `NuGet.Config` 用相对路径引用它，clone 后无需配置任何本机路径。

```bash
./build.sh
./build.sh -c Release          # 透传任意 dotnet build 参数
dotnet run --project src/FileHub.Desktop/FileHub.Desktop.csproj
```

`build.sh` 带资源护栏（`nice -n 20`、`-m:2`、`-nodeReuse:false`），并按下述优先级挑 SDK：

1. 环境变量 `FILEHUB_DOTNET`
2. `$HOME/dotnet11x64/dotnet`（若存在）
3. PATH 里的 `dotnet`

即普通机器上直接用官方 .NET 10 SDK 就行；只有遇到下面这两个环境级问题时才需要指定 SDK。

### 为什么有 `FileHubCompileAvaloniaXaml` 这个 target

CodeNet **arm64** SDK（10.0.400）把 `Microsoft.CSharp.CurrentVersion.targets` 里
`CallTarget Targets="$(TargetsTriggeredByCompilation)"` 整段注释掉了，而 Avalonia 12 的
`CompileAvaloniaXaml` 正是靠这个钩子触发的。后果是：构建显示 0 错误，产物里却只有原始 XAML，
运行时报 `No precompiled XAML found for FileHub.Desktop.App`。

`src/FileHub.Desktop/FileHub.Desktop.csproj` 里加了一个 `AfterTargets="CoreCompile"` 的显式调用，
强制编译 XAML（不依赖 SDK 钩子，换任何 SDK 都生效）。另外 Avalonia 12.1 默认开启编译绑定，
本项目用反射绑定，所以显式设了 `AvaloniaUseCompiledBindingsByDefault=false`。
官方 SDK 与 CodeNet x64 SDK 自带该钩子，而 MSBuild 保证同一 Target 在一次构建中只执行一次，
重复声明不会二次编译，CI 走官方 SDK 也安全。

⚠️ 已知环境限制：**arm64 CodeNet SDK 在执行 XAML 编译任务时会 SIGSEGV(139)**（signalfix 的老问题）。
官方 arm64 SDK 或 x64 SDK 均可，作者机器上实测 x64 SDK 7s / 0 警告 0 错误。

### 关于 `local-feed/`

`CodeHub-FluentUI 3.1.0-codehub.1` 源自
[`Hub-code-develop/CodeHub-FluentUI`](https://github.com/Hub-code-develop/CodeHub-FluentUI)（MIT），
未发布到 nuget.org。为了让仓库自包含，把 1.1 MB 的 nupkg 一并提交。
详见 `local-feed/README.md`；若该包日后上了 nuget.org，删掉 `NuGet.Config` 里的
`codehub-local` 与这个目录即可。包依赖 Avalonia 12.1.0 / net10.0，版本不要随意改。

## GitHub Actions

`.github/workflows/build.yml` 在 push 到 `main`、PR 和手动触发时跑三平台矩阵：

| Runner | RID | 流程 |
| --- | --- | --- |
| `ubuntu-latest` | `linux-x64` | restore → Release build → RID restore → publish → XAML 门禁 → 上传 artifact |
| `windows-latest` | `win-x64` | 同上 |
| `macos-latest` | `osx-arm64` | 同上 |

- SDK 由 `actions/setup-dotnet@v4` 按 `global.json`（10.0.400）安装，不需要任何密钥
- `dotnet restore --configfile NuGet.Config` 显式使用仓库自带的 `local-feed/`，私有源 / token 都不需要
- 解决方案级 `dotnet build` 不接受 `-r`（NETSDK1134），所以 RID 只加在单项目 `dotnet publish` 上；
  带 RID 的 framework-dependent publish 只带本平台 native 库，约 **31 MB**（不带 RID 会摊平成 564 MB，大半是 Windows 的 `.pdb`）
- **XAML 门禁**：检查 `publish/FileHub.dll` 里必须含 `!XamlIlPopulate`，
  专门防「构建显示 0 错误但 XAML 没预编译」这个坑再次溜过去
- 每个平台上传 `FileHub-<rid>-net10.0` artifact（`publish/`，保留 14 天）

## 结构

```
FileHub/
├── .github/workflows/     build.yml（三平台 CI）
├── FileHub.slnx           解决方案
├── global.json            SDK 10.0.400
├── NuGet.Config           nuget.org + 仓库内离线源
├── local-feed/            CodeHub-FluentUI 的 nupkg（MIT）
├── build.sh               带资源护栏的构建脚本
└── src/
    ├── FileHub.Files/     纯逻辑，无 UI 依赖，可单测
    │   ├── Models/        FileEntry / LocationEntry
    │   └── Services/      FileSystemService / RecycleBinService / PlatformService / FileClipboard
    └── FileHub.Desktop/   Avalonia 应用
        ├── App.axaml      主题入口
        ├── Styles/        应用样式
        ├── ViewModels/    MainViewModel + 命令 + 条目 VM
        ├── Views/         MainWindow
        └── Services/      DialogService（FAContentDialog 封装）
```

## 验证情况

- `build.sh`：0 警告 0 错误，XAML 已编译进 `FileHub.dll`（可见 `!XamlIlPopulate`）
- 全量改名后重新做无窗口 headless 验证：`App.axaml` + `FluentAvaloniaTheme` +
  `avares://FileHub/Styles/AppStyles.axaml` 加载成功；`MainWindow.axaml` 全部控件实例化成功；
  主目录枚举 55 项 / 7 个位置 / 3 段面包屑；筛选 55→0→55 正常
- 未做真实 GUI 窗口冒烟测试

## 已知限制 / 后续可加

- 单窗口，暂未做多标签页（库里有 `FATabView`，可以接）
- 没有拖拽移动、缩略图预览、文件监视（目录变化需手动刷新）
- 递归搜索固定 300 项上限，避免在大目录里失控
- 复制/移动为应用内剪贴板，不写系统剪贴板（只复制路径会写）
