# FileHub

一个用 **.NET 10 + Avalonia 12 + FluentUI-CodeHub（`CodeHub-FluentUI`）** 写的跨平台文件管理器。
仓库名 FileHub，项目/程序集内部代号 **Cobalt**（钴）。

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
dotnet run --project src/Cobalt.Desktop/Cobalt.Desktop.csproj
```

`build.sh` 带资源护栏（`nice -n 20`、`-m:2`、`-nodeReuse:false`），并按下述优先级挑 SDK：

1. 环境变量 `COBALT_DOTNET`
2. `$HOME/dotnet11x64/dotnet`（若存在）
3. PATH 里的 `dotnet`

即普通机器上直接用官方 .NET 10 SDK 就行；只有遇到下面这两个环境级问题时才需要指定 SDK。

### 为什么有 `CobaltCompileAvaloniaXaml` 这个 target

CodeNet 的 .NET 10.0.400 SDK 把 `Microsoft.CSharp.CurrentVersion.targets` 里
`CallTarget Targets="$(TargetsTriggeredByCompilation)"` 整段注释掉了，而 Avalonia 12 的
`CompileAvaloniaXaml` 正是靠这个钩子触发的。后果是：构建显示 0 错误，产物里却只有原始 XAML，
运行时报 `No precompiled XAML found for Cobalt.Desktop.App`。

`src/Cobalt.Desktop/Cobalt.Desktop.csproj` 里加了一个 `AfterTargets="CoreCompile"` 的显式调用，
强制编译 XAML（不依赖 SDK 钩子，换任何 SDK 都生效）。另外 Avalonia 12.1 默认开启编译绑定，
本项目用反射绑定，所以显式设了 `AvaloniaUseCompiledBindingsByDefault=false`。

⚠️ 已知环境限制：**arm64 CodeNet SDK 在执行 XAML 编译任务时会 SIGSEGV(139)**（signalfix 的老问题）。
官方 arm64 SDK 或 x64 SDK 均可，作者机器上实测 x64 SDK 15s / 0 警告 0 错误。

### 关于 `local-feed/`

`CodeHub-FluentUI 3.1.0-codehub.1` 源自
[`Hub-code-develop/CodeHub-FluentUI`](https://github.com/Hub-code-develop/CodeHub-FluentUI)（MIT），
未发布到 nuget.org。为了让仓库自包含，把 1.1 MB 的 nupkg 一并提交。
详见 `local-feed/README.md`；若该包日后上了 nuget.org，删掉 `NuGet.Config` 里的
`codehub-local` 与这个目录即可。包依赖 Avalonia 12.1.0 / net10.0，版本不要随意改。

## 结构

```
FileHub/
├── Cobalt.slnx            解决方案
├── global.json            SDK 10.0.400
├── NuGet.Config           nuget.org + 仓库内离线源
├── local-feed/            CodeHub-FluentUI 的 nupkg（MIT）
├── build.sh               带资源护栏的构建脚本
└── src/
    ├── Cobalt.Files/      纯逻辑，无 UI 依赖，可单测
    │   ├── Models/        FileEntry / LocationEntry
    │   └── Services/      FileSystemService / RecycleBinService / PlatformService / FileClipboard
    └── Cobalt.Desktop/    Avalonia 应用
        ├── App.axaml      主题入口
        ├── Styles/        应用样式
        ├── ViewModels/    MainViewModel + 命令 + 条目 VM
        ├── Views/         MainWindow
        └── Services/      DialogService（FAContentDialog 封装）
```

## 验证情况

- `build.sh`：0 警告 0 错误，XAML 已编译进 `Cobalt.dll`（可见 `!XamlIlPopulate`）
- 无窗口 headless 验证：`App.axaml` + `FluentAvaloniaTheme` + `AppStyles.axaml` 加载成功；
  `MainWindow.axaml` 全部控件实例化成功；主目录枚举 54 项 / 7 个位置 / 3 段面包屑；
  筛选 54→0→54 正常
- 未做真实 GUI 窗口冒烟测试

## 已知限制 / 后续可加

- 单窗口，暂未做多标签页（库里有 `FATabView`，可以接）
- 没有拖拽移动、缩略图预览、文件监视（目录变化需手动刷新）
- 递归搜索固定 300 项上限，避免在大目录里失控
- 复制/移动为应用内剪贴板，不写系统剪贴板（只复制路径会写）
