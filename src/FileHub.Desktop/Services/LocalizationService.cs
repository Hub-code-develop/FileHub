// String table. The first value of every entry is English, the second is the zh-CN translation.
// Which one the app displays is decided by the OPERATING SYSTEM language at startup; English is
// never a hard-coded default for the UI. It is only the fallback when a locale has no translation
// yet, and the default language for documentation.
// XAML reads these through Strings.* (x:Static); C# reads them through Loc.T / Loc.F.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace FileHub.Desktop.Services;

/// <summary>Minimal built-in localization. Every user-visible string has an English and a Chinese value.</summary>
public static class Loc
{
    private static readonly Dictionary<string, string[]> Table = new()
    {
        ["AppTitle"] = new[] { "FileHub - Cross-platform file manager", "FileHub - 跨平台文件管理器" },
        ["PaneTitle"] = new[] { "FileHub", "FileHub" },
        ["QuickAccess"] = new[] { "Quick access", "快速访问" },
        ["Drives"] = new[] { "Drives", "驱动器" },
        ["NewTab"] = new[] { "New tab (Ctrl+T)", "新建标签页 (Ctrl+T)" },
        ["CloseTab"] = new[] { "Close tab (Ctrl+W)", "关闭标签页 (Ctrl+W)" },
        ["NextTab"] = new[] { "Next tab (Ctrl+Tab)", "下一个标签页 (Ctrl+Tab)" },
        ["PreviousTab"] = new[] { "Previous tab (Ctrl+Shift+Tab)", "上一个标签页 (Ctrl+Shift+Tab)" },
        ["ReopenClosedTab"] = new[] { "Reopen closed tab (Ctrl+Shift+T)", "重新打开关闭的标签页 (Ctrl+Shift+T)" },
        ["CloseOtherTabs"] = new[] { "Close other tabs", "关闭其他标签页" },
        ["CloseTabsToRight"] = new[] { "Close tabs to the right", "关闭右侧标签页" },
        ["DuplicateTab"] = new[] { "Duplicate tab", "复制标签页" },
        ["MoveTabLeft"] = new[] { "Move tab left", "标签左移" },
        ["MoveTabRight"] = new[] { "Move tab right", "标签右移" },
        ["TabsMenu"] = new[] { "Tabs", "标签页" },
        ["NoClosedTabs"] = new[] { "No recently closed tabs", "没有最近关闭的标签页" },
        ["SwitchLanguage"] = new[] { "Language: Auto / English / Chinese (click to change)", "语言：自动 / English / 中文（点击切换）" },
        ["BuildChannel"] = new[] { "Channel", "发布通道" },
        ["BuildVersion"] = new[] { "Version", "版本" },
        ["BuildChannel_dev"] = new[] { "Local development build", "本地开发构建" },
        ["BuildChannel_stable"] = new[] { "Stable release", "正式版" },
        ["BuildChannel_nightly"] = new[] { "Nightly build", "每日夜间构建" },
        ["BuildChannel_preview"] = new[] { "Preview build", "预览版" },
        ["LangAutoHint"] = new[] { "Following the OS language ({0}). Click to override.", "跟随系统语言（{0}），点击可手动切换" },
        ["LangManualHint"] = new[] { "Manual override for this session. Click again to follow the OS.", "本次运行手动指定。再点一次恢复跟随系统" },
        ["DetailsPane"] = new[] { "Details", "详细信息" },
        ["ToggleDetailsPane"] = new[] { "Toggle details pane", "显示/隐藏详细信息窗格" },
        ["NoSelection"] = new[] { "Select an item to see its details", "选择一个项目以查看详细信息" },
        ["Back"] = new[] { "Back (Alt+Left)", "后退 (Alt+Left)" },
        ["Forward"] = new[] { "Forward (Alt+Right)", "前进 (Alt+Right)" },
        ["Up"] = new[] { "Up (Backspace)", "上一级 (Backspace)" },
        ["Refresh"] = new[] { "Refresh (F5)", "刷新 (F5)" },
        ["NewFolder"] = new[] { "New folder", "新建文件夹" },
        ["Copy"] = new[] { "Copy (Ctrl+C)", "复制 (Ctrl+C)" },
        ["Cut"] = new[] { "Cut (Ctrl+X)", "剪切 (Ctrl+X)" },
        ["Paste"] = new[] { "Paste (Ctrl+V)", "粘贴 (Ctrl+V)" },
        ["Rename"] = new[] { "Rename (F2)", "重命名 (F2)" },
        ["Delete"] = new[] { "Move to recycle bin (Delete)", "移到回收站 (Delete)" },
        ["ToggleHidden"] = new[] { "Show/hide hidden items", "显示/隐藏隐藏文件" },
        ["ToggleView"] = new[] { "Switch layout", "切换视图" },
        ["ToggleTheme"] = new[] { "Switch theme", "切换主题" },
        ["Sort"] = new[] { "Sort", "排序" },
        ["Layout"] = new[] { "Layout", "布局" },
        ["EditAddress"] = new[] { "Edit path (Ctrl+L)", "编辑路径 (Ctrl+L)" },
        ["DeepSearch"] = new[] { "Search recursively in this folder", "在当前目录递归搜索" },
        ["AddressPlaceholder"] = new[] { "Enter a full path and press Enter (~ supported)", "输入完整路径后回车，支持 ~ 开头" },
        ["SearchPlaceholder"] = new[] { "Filter current folder...", "筛选当前目录..." },
        ["ColName"] = new[] { "Name", "名称" },
        ["ColSize"] = new[] { "Size", "大小" },
        ["ColKind"] = new[] { "Type", "类型" },
        ["ColModified"] = new[] { "Date modified", "修改时间" },
        ["EmptyFolder"] = new[] { "This folder is empty", "这个文件夹是空的" },
        ["CtxOpen"] = new[] { "Open", "打开" },
        ["CtxReveal"] = new[] { "Reveal in system file manager", "在系统文件管理器中显示" },
        ["CtxCopy"] = new[] { "Copy", "复制" },
        ["CtxCut"] = new[] { "Cut", "剪切" },
        ["CtxPaste"] = new[] { "Paste", "粘贴" },
        ["CtxCopyPath"] = new[] { "Copy path", "复制路径" },
        ["CtxNewFolder"] = new[] { "New folder", "新建文件夹" },
        ["CtxRename"] = new[] { "Rename", "重命名" },
        ["CtxDelete"] = new[] { "Move to recycle bin", "移到回收站" },
        ["CtxSelectAll"] = new[] { "Select all", "全选" },
        ["CtxProperties"] = new[] { "Properties", "属性" },
        ["DlgPlaceholder"] = new[] { "Enter a name", "输入名称" },
        ["DlgCancel"] = new[] { "Cancel", "取消" },
        ["DlgOK"] = new[] { "OK", "确定" },
        ["DlgClose"] = new[] { "Close", "关闭" },
        ["DlgNewFolderTitle"] = new[] { "New folder", "新建文件夹" },
        ["DlgNewFolderMessage"] = new[] { "Will be created in {0}", "将在 {0} 创建" },
        ["DlgNewFolderDefault"] = new[] { "New folder", "新建文件夹" },
        ["DlgCreate"] = new[] { "Create", "创建" },
        ["DlgRenameTitle"] = new[] { "Rename", "重命名" },
        ["DlgRenameMessage"] = new[] { "Current name: {0}", "当前名称：{0}" },
        ["DlgRenameButton"] = new[] { "Rename", "重命名" },
        ["DlgDeleteTitle"] = new[] { "Move to recycle bin", "移到回收站" },
        ["DlgDeleteMessage"] = new[] { "Move {0} item(s) to the recycle bin?\n\n{1}", "确定要把这 {0} 个项目移到回收站吗？\n\n{1}" },
        ["DlgPropertiesTitle"] = new[] { "Properties", "属性" },
        ["MsgInvalidPath"] = new[] { "Invalid path", "路径无效" },
        ["MsgCannotOpenLocation"] = new[] { "Cannot open this location", "打不开这个位置" },
        ["MsgLocationDetail"] = new[] { "Folder does not exist or is not accessible: {0}", "目录不存在或没有权限：{0}" },
        ["MsgReadFailed"] = new[] { "Failed to read folder", "读取目录失败" },
        ["MsgSearch"] = new[] { "Search", "搜索" },
        ["MsgSearchEmpty"] = new[] { "Enter a name to search for", "先输入要查找的名称" },
        ["MsgSearchBreadcrumb"] = new[] { "Search \"{0}\"", "搜索 \"{0}\"" },
        ["MsgSearchDone"] = new[] { "Search complete", "搜索完成" },
        ["MsgSearchDoneBody"] = new[] { "Found {0} item(s) under {1}", "在 {1} 下找到 {0} 项" },
        ["MsgSearchFailed"] = new[] { "Search failed", "搜索失败" },
        ["MsgCannotOpen"] = new[] { "Cannot open", "无法打开" },
        ["MsgCreatedFolder"] = new[] { "Folder created", "已创建文件夹" },
        ["MsgCreateFailed"] = new[] { "Create failed", "创建失败" },
        ["MsgRenameDone"] = new[] { "Renamed", "重命名完成" },
        ["MsgRenameFailed"] = new[] { "Rename failed", "重命名失败" },
        ["MsgMovedToTrash"] = new[] { "Moved to recycle bin", "已移到回收站" },
        ["MsgDeleteFailed"] = new[] { "Delete failed", "删除失败" },
        ["MsgCopied"] = new[] { "Copied", "已复制" },
        ["MsgCut"] = new[] { "Cut", "已剪切" },
        ["MsgClipboardHint"] = new[] { "{0} item(s) - paste in the target folder", "{0} 个项目 - 到目标文件夹后按粘贴" },
        ["MsgPasteDone"] = new[] { "Paste complete", "粘贴完成" },
        ["MsgPasteBody"] = new[] { "{0} item(s) {1} to this folder", "{0} 个项目已{1}到当前目录" },
        ["MsgCopiedWord"] = new[] { "copied", "复制" },
        ["MsgMovedWord"] = new[] { "moved", "移动" },
        ["MsgPasteFailed"] = new[] { "Paste failed", "粘贴失败" },
        ["MsgPathCopied"] = new[] { "Path copied", "路径已复制" },
        ["MsgCannotReveal"] = new[] { "Cannot reveal", "无法显示" },
        ["MsgOperationFailed"] = new[] { "Operation failed", "操作失败" },
        ["PropFolder"] = new[] { "Type: Folder\nPath: {0}\nModified: {1}\nContains: {2} item(s)", "类型：文件夹\n路径：{0}\n修改时间：{1}\n包含：{2} 个项目" },
        ["PropFile"] = new[] { "Type: {0}\nPath: {1}\nSize: {2} ({3} bytes)\nModified: {4}", "类型：{0}\n路径：{1}\n大小：{2}（{3} 字节）\n修改时间：{4}" },
        ["Unknown"] = new[] { "Unknown", "未知" },
        ["SortName"] = new[] { "Name", "名称" },
        ["SortSize"] = new[] { "Size", "大小" },
        ["SortKind"] = new[] { "Type", "类型" },
        ["SortModified"] = new[] { "Date modified", "修改时间" },
        ["SortAsc"] = new[] { " (asc)", "（升序）" },
        ["SortDesc"] = new[] { " (desc)", "（降序）" },
        ["ThemeSystem"] = new[] { "Follow system", "跟随系统" },
        ["ThemeLight"] = new[] { "Light", "浅色" },
        ["ThemeDark"] = new[] { "Dark", "深色" },
        ["StatusPreparing"] = new[] { "Preparing...", "正在准备..." },
        ["StatusItems"] = new[] { "{0} item(s)", "{0} 个项目" },
        ["StatusSearchResults"] = new[] { "Search results: {0} (limit 300)", "搜索结果 {0} 项（上限 300）" },
        ["StatusFiltered"] = new[] { "Showing {0} of {1} item(s)", "筛选出 {0} 项 / 共 {1} 项" },
        ["SelectionText"] = new[] { "Selected {0} item(s) - {1}", "已选 {0} 项 - {1}" },
        ["KindFolder"] = new[] { "Folder", "文件夹" },
        ["KindFile"] = new[] { "File", "文件" },
        ["KindExtension"] = new[] { "{0} File", "{0} 文件" },
        ["LocHome"] = new[] { "Home", "主目录" },
        ["LocDesktop"] = new[] { "Desktop", "桌面" },
        ["LocDownloads"] = new[] { "Downloads", "下载" },
        ["LocDocuments"] = new[] { "Documents", "文档" },
        ["LocPictures"] = new[] { "Pictures", "图片" },
        ["LocVideos"] = new[] { "Videos", "视频" },
        ["PaneName"] = new[] { "Name", "名称" },
        ["PaneType"] = new[] { "Type", "类型" },
        ["PaneSize"] = new[] { "Size", "大小" },
        ["PaneModified"] = new[] { "Date modified", "修改时间" },
        ["PanePath"] = new[] { "Path", "路径" },
        ["PaneContains"] = new[] { "Contains", "包含" },
        ["TabHome"] = new[] { "Home", "主目录" },
        ["PlatformText"] = new[] { ".NET 10 | Avalonia 12 | FluentUI-CodeHub", ".NET 10 | Avalonia 12 | FluentUI-CodeHub" },
        ["LblNew"] = new[] { "New", "新建" },
        ["LblCut"] = new[] { "Cut", "剪切" },
        ["LblCopy"] = new[] { "Copy", "复制" },
        ["LblPaste"] = new[] { "Paste", "粘贴" },
        ["LblRename"] = new[] { "Rename", "重命名" },
        ["LblDelete"] = new[] { "Delete", "删除" },
        ["LblSort"] = new[] { "Sort", "排序" },
        ["LblLayout"] = new[] { "Layout", "布局" },
        ["LblDetails"] = new[] { "Details", "详细信息" },
        ["LblMore"] = new[] { "More", "更多" },
        ["LblHidden"] = new[] { "Hidden", "隐藏项目" },
        ["LblSettings"] = new[] { "Settings", "设置" },
        ["LblPreview"] = new[] { "Preview", "预览" },
        ["LblOpen"] = new[] { "Open", "打开" },
        ["LblProperties"] = new[] { "Properties", "属性" },
        ["LblToggleSidebar"] = new[] { "Toggle sidebar", "展开/收起侧栏" },
        ["LblStatusCenter"] = new[] { "Status center", "状态中心" },
        ["LblNoPreview"] = new[] { "No preview available", "暂无预览" },
        ["LblView"] = new[] { "View", "视图" },
        ["LblSelect"] = new[] { "Select", "选择" },
        ["MsgDropDone"] = new[] { "Moved", "已移动" },
        ["MsgDropCopied"] = new[] { "Copied", "已复制" },
        ["MsgDropBody"] = new[] { "{0} item(s) to {1}", "{0} 个项目到 {1}" },
        ["MsgDropFailed"] = new[] { "Drag and drop failed", "拖放失败" },
        ["MsgDropInvalid"] = new[] { "Cannot drop a folder into itself", "不能把文件夹拖进它自己的子目录" },
        ["MsgWatcherChanged"] = new[] { "Folder changed on disk - list refreshed", "目录内容已变化 - 列表已刷新" },
        ["CodeTab"] = new[] { "Code", "代码" },
        ["OfficeTab"] = new[] { "Office", "Office" },
        ["OfficeWord"] = new[] { "Word document", "Word 文档" },
        ["OfficeExcel"] = new[] { "Excel workbook", "Excel 工作簿" },
        ["OfficePowerPoint"] = new[] { "PowerPoint presentation", "PowerPoint 演示文稿" },
        ["OfficeTabFile"] = new[] { "File", "文件" },
        ["OfficeTabHome"] = new[] { "Home", "开始" },
        ["OfficeTabInsert"] = new[] { "Insert", "插入" },
        ["OfficeTabView"] = new[] { "View", "视图" },
        ["OfficeGroupFile"] = new[] { "File", "文件" },
        ["OfficeGroupFont"] = new[] { "Font", "字体" },
        ["OfficeGroupCells"] = new[] { "Cells", "单元格" },
        ["OfficeGroupSlides"] = new[] { "Slides", "幻灯片" },
        ["OfficeNew"] = new[] { "New", "新建" },
        ["OfficeNewHint"] = new[] { "Create a blank Word document in your home folder", "在主目录创建空白 Word 文档" },
        ["OfficeNewNameHint"] = new[] { "New document name", "新文档名称" },
        ["OfficeSave"] = new[] { "Save", "保存" },
        ["OfficeBold"] = new[] { "Bold", "粗体" },
        ["OfficeItalic"] = new[] { "Italic", "斜体" },
        ["OfficeUnderline"] = new[] { "Underline", "下划线" },
        ["OfficeOpened"] = new[] { "Opened {0}", "已打开 {0}" },
        ["OfficeSaved"] = new[] { "Saved", "已保存" },
        ["OfficeOpenFailed"] = new[] { "Cannot open document: {0}", "无法打开文档：{0}" },
        ["OfficeSaveFailed"] = new[] { "Cannot save: {0}", "无法保存：{0}" },
        ["OfficeCreateFailed"] = new[] { "Cannot create document: {0}", "无法创建文档：{0}" },
        ["OfficeEmpty"] = new[] { "No document open", "未打开文档" },
        ["OfficeEmptyHint"] = new[] { "Double-click a Word, Excel or PowerPoint file in FileHub to open it here", "在 FileHub 中双击 Word、Excel 或 PowerPoint 文件即可在此打开" },
        ["OfficeExcelHint"] = new[] { "Click a cell to edit its value. Save writes changes back to the workbook.", "点击单元格编辑内容，保存后写回工作簿。" },
        ["OfficeReadOnlyHint"] = new[] { "Slide outline view - editing is not supported", "幻灯片大纲视图，不支持编辑" },
        ["OfficeDisclaimer"] = new[] { "Light editing via Open XML SDK; complex formatting may be simplified", "基于 Open XML SDK 的轻量编辑，复杂格式可能被简化" },

        ["ExtensionsTab"] = new[] { "Extensions", "扩展" },
        ["ClosePage"] = new[] { "Close page", "关闭页面" },
        ["CodeTitle"] = new[] { "Code editor", "代码编辑器" },
        ["CodeOpenFile"] = new[] { "Open file", "打开文件" },
        ["CodeSave"] = new[] { "Save", "保存" },
        ["CodeLanguage"] = new[] { "Language", "语言" },
        ["CodePlainText"] = new[] { "Plain text", "纯文本" },
        ["CodeEncoding"] = new[] { "Encoding", "编码" },
        ["CodeCursor"] = new[] { "Ln {0}, Col {1}", "行 {0}，列 {1}" },
        ["CodeLoaded"] = new[] { "Loaded {0} characters", "已加载 {0} 个字符" },
        ["CodeSaved"] = new[] { "Saved", "已保存" },
        ["CodeSaveFailed"] = new[] { "Save failed: {0}", "保存失败：{0}" },
        ["CodeTheme"] = new[] { "Theme", "主题" },
        ["CodeThemeDefault"] = new[] { "Default theme", "默认主题" },
        ["CodeThemeAuto"] = new[] { "Use VS Code theme", "使用 VS Code 主题" },
        ["CodeThemeFailed"] = new[] { "Theme failed: {0}", "主题加载失败：{0}" },
        ["CodeEngineLoading"] = new[] { "Loading editor engine...", "正在加载编辑器引擎..." },
        ["CodeNoFile"] = new[] { "Open a code file from the file list to start editing", "从文件列表打开代码文件即可编辑" },
        ["CodeCloseHint"] = new[] { "Close editor", "关闭编辑器" },
        ["ExtTitle"] = new[] { "VS Code extensions", "VS Code 扩展" },
        ["ExtSearchPlaceholder"] = new[] { "Search installed extensions...", "搜索已安装扩展..." },
        ["ExtCount"] = new[] { "{0} installed, {1} enabled", "共 {0} 个，已启用 {1} 个" },
        ["ExtRoots"] = new[] { "Scanned: {0}", "扫描位置：{0}" },
        ["ExtNoRoots"] = new[] { "No VS Code extension folder found", "未找到 VS Code 扩展目录" },
        ["ExtEnable"] = new[] { "Enable", "启用" },
        ["ExtDisable"] = new[] { "Disable", "禁用" },
        ["ExtOpenFolder"] = new[] { "Open folder", "打开文件夹" },
        ["ExtRefresh"] = new[] { "Rescan", "重新扫描" },
        ["ExtSelectHint"] = new[] { "Select an extension to see its contributions", "选择一个扩展查看其贡献内容" },
        ["ExtLanguages"] = new[] { "Languages: {0}", "语言：{0}" },
        ["ExtThemes"] = new[] { "Themes: {0}", "主题：{0}" },
        ["ExtSnippets"] = new[] { "Snippet sets: {0}", "代码片段集：{0}" },
        ["ExtGrammars"] = new[] { "Grammars: {0}", "语法：{0}" },
        ["ExtCommands"] = new[] { "Commands: {0}", "命令：{0}" },
        ["ExtFolder"] = new[] { "Folder: {0}", "文件夹：{0}" },
        ["ExtOpenFailed"] = new[] { "Could not open folder: {0}", "无法打开文件夹：{0}" },
        ["ExtNotice"] = new[] { "FileHub reads extension manifests for languages, themes and snippets. It does not run extension JavaScript.", "FileHub 读取扩展清单中的语言、主题和代码片段；不执行扩展 JavaScript。" },

        // Visual Studio Marketplace. "Visual Studio Marketplace" is a Microsoft trademark;
        // FileHub is not an official Microsoft client.
        ["MktTitle"] = new[] { "Marketplace", "官方商店" },
        ["MktInstalledTab"] = new[] { "Installed", "已安装" },
        ["MktMarketplaceTab"] = new[] { "Marketplace", "官方商店" },
        ["MktSearchPlaceholder"] = new[] { "Search the Visual Studio Marketplace...", "搜索 Visual Studio Marketplace..." },
        ["MktSearch"] = new[] { "Search", "搜索" },
        ["MktCount"] = new[] { "{0} results", "共 {0} 个结果" },
        ["MktSearching"] = new[] { "Searching for \"{0}\"...", "正在搜索“{0}”..." },
        ["MktNoResults"] = new[] { "No extensions found", "没有找到扩展" },
        ["MktSelectHint"] = new[] { "Select an extension from the Marketplace to see its details", "从官方商店选择一个扩展查看详情" },
        ["MktInstall"] = new[] { "Install", "安装" },
        ["MktIncompatible"] = new[] { "Not available for this platform", "此平台不可用" },
        ["MktDownloading"] = new[] { "Downloading {0}...", "正在下载 {0}..." },
        ["MktInstalling"] = new[] { "Installing {0}...", "正在安装 {0}..." },
        ["MktInstalled"] = new[] { "Installed {0}. Reopen code files to use new contributions.", "已安装 {0}。重新打开代码文件即可使用新的贡献内容。" },
        ["MktUninstall"] = new[] { "Uninstall", "卸载" },
        ["MktUninstalled"] = new[] { "Uninstalled {0}", "已卸载 {0}" },
        ["MktNotManaged"] = new[] { "This extension was not installed by FileHub", "此扩展不是由 FileHub 安装的" },
        ["MktFailed"] = new[] { "Marketplace request failed: {0}", "商店请求失败：{0}" },
        ["MktPublisher"] = new[] { "Publisher: {0}", "发布者：{0}" },
        ["MktInstalls"] = new[] { "Installs: {0}", "安装量：{0}" },
        ["MktRating"] = new[] { "Rating: {0:F1} / 5 ({1} ratings)", "评分：{0:F1} / 5（{1} 人评分）" },
        ["MktPlatform"] = new[] { "Platform: {0}", "平台：{0}" },
        ["MktPlatformDetail"] = new[] { "Package: {0}", "安装包：{0}" },
        ["MktUniversal"] = new[] { "universal", "通用" },
        ["MktOpenStore"] = new[] { "Open on Marketplace", "在商店中打开" },
        ["MktLicense"] = new[] { "License", "许可证" },
        ["MktNotice"] = new[] { "Packages are downloaded on request and installed into FileHub's own extension folder. No extension is bundled with FileHub. FileHub is not affiliated with Microsoft and does not run extension JavaScript.", "安装包按需下载并安装到 FileHub 自己的扩展目录；FileHub 不随包分发任何扩展。FileHub 与 Microsoft 无隶属关系，也不执行扩展 JavaScript。" },
    };

    /// <summary>"en" (default) or "zh-CN".</summary>
    private const string English = "en";
    private const string Chinese = "zh-CN";

    /// <summary>Set while DetectOsLanguage() runs so the UI can show where the language came from.</summary>
    public static string DetectionSource { get; private set; } = "unknown";

    /// <summary>The language detected from the OS, captured once before any manual override.</summary>
    public static string DetectedLanguage { get; } = DetectOsLanguage();

    /// <summary>True while the UI follows the OS language; false after a manual override.</summary>
    public static bool IsAuto { get; private set; } = true;

    /// <summary>The language used for every UI string right now.</summary>
    public static string Language { get; private set; } = ResolveStartupLanguage();

    public static string T(string key)
        => Table.TryGetValue(key, out var v) ? v[Language == Chinese ? 1 : 0] : key;

    public static string F(string key, params object?[] args) => string.Format(T(key), args);

    /// <summary>
    /// "auto" restores OS detection; "en" / "zh-CN" override it for the current session only.
    /// A manual choice is never persisted, so the next launch follows the OS again.
    /// </summary>
    public static void SetLanguage(string language)
    {
        var value = language?.Trim() ?? "auto";
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase)
            || value.Equals("system", StringComparison.OrdinalIgnoreCase)
            || value.Equals("os", StringComparison.OrdinalIgnoreCase))
        {
            IsAuto = true;
            Language = Normalize(DetectedLanguage);
        }
        else
        {
            IsAuto = false;
            Language = Normalize(value);
        }
    }

    /// <summary>Cycles Auto -> English -> Chinese -> Auto.</summary>
    public static string NextLanguage()
        => IsAuto ? English : (Language == English ? Chinese : "auto");

    public static string CurrentLanguageLabel
        => IsAuto ? (Language == Chinese ? "自动" : "Auto")
                  : (Language == English ? "EN" : "中");

    private static string ResolveStartupLanguage()
    {
        var forced = Environment.GetEnvironmentVariable("FILEHUB_LANG")?.Trim();
        if (string.IsNullOrEmpty(forced)
            || forced.Equals("auto", StringComparison.OrdinalIgnoreCase)
            || forced.Equals("system", StringComparison.OrdinalIgnoreCase)
            || forced.Equals("os", StringComparison.OrdinalIgnoreCase))
            return Normalize(DetectedLanguage);

        IsAuto = false;
        return Normalize(forced);
    }

    /// <summary>
    /// Reads the language configured in the operating system, not the language of the shell that
    /// happened to launch the process. Order matters: a GUI app launched from Finder / Explorer
    /// inherits no LC_* variables, so the platform-specific preference must be queried first.
    /// </summary>
    private static string DetectOsLanguage()
    {
        // macOS: the user's preferred language list (System Settings > General > Language & Region).
        if (OperatingSystem.IsMacOS())
        {
            var tag = FirstValue(RunDefaults("read -g AppleLanguages"))
                      ?? FirstValue(RunDefaults("read -g AppleLocale"));
            if (!string.IsNullOrWhiteSpace(tag))
            {
                DetectionSource = "macOS AppleLanguages";
                return tag;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            // On Windows .NET reports the actual user UI culture rather than a process locale.
            var installed = CultureInfo.InstalledUICulture?.Name;
            if (!string.IsNullOrWhiteSpace(installed))
            {
                DetectionSource = "Windows InstalledUICulture";
                return installed;
            }
        }
        else
        {
            // Linux / Unix: standard locale precedence. "C" and "C.UTF-8" carry no language
            // information, so they are skipped instead of being mistaken for English.
            foreach (var name in new[] { "LC_ALL", "LC_MESSAGES", "LANG" })
            {
                var value = Environment.GetEnvironmentVariable(name);
                if (string.IsNullOrWhiteSpace(value)) continue;
                var bare = value.Split('.', '@', ':')[0].Trim();
                if (bare is "C" or "POSIX" || string.IsNullOrEmpty(bare)) continue;
                DetectionSource = name;
                return bare;
            }
        }

        var culture = CultureInfo.CurrentUICulture?.Name;
        if (!string.IsNullOrWhiteSpace(culture) && culture != "iv")
        {
            DetectionSource = "CultureInfo.CurrentUICulture";
            return culture;
        }

        // No OS signal at all. This is the only path that returns English, and it is a
        // "no information" fallback, not a default for the application.
        DetectionSource = "fallback (no OS language signal)";
        return English;
    }

    /// <summary>Runs the macOS preferences CLI with a timeout; never throws into UI startup.</summary>
    private static string? RunDefaults(string arguments)
    {
        try
        {
            var exe = File.Exists("/usr/bin/defaults") ? "/usr/bin/defaults" : "defaults";
            using var process = Process.Start(new ProcessStartInfo(exe, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null) return null;
            if (!process.WaitForExit(1500))
            {
                try { process.Kill(true); } catch { /* best effort */ }
                return null;
            }
            return process.StandardOutput.ReadToEnd();
        }
        catch { return null; }
    }

    /// <summary>Picks the first language tag out of `( "zh-Hans-CN", "en-CN" )` or a plain value.</summary>
    private static string? FirstValue(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var match = Regex.Match(output, "\"([^\"]+)\"");
        if (match.Success) return match.Groups[1].Value.Trim();
        var first = output.Trim().Split('\n')[0].Trim();
        return first.Length == 0 ? null : first;
    }

    /// <summary>
    /// Maps an OS language tag to one of the two available translations. Chinese variants
    /// (zh-Hans, zh-Hant, yue, ...) all use zh-CN for now. Unknown locales fall back to English
    /// only because no translation exists for them yet.
    /// </summary>
    private static string Normalize(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return English;
        var primary = tag.Replace('_', '-').Split('-')[0].Trim().ToLowerInvariant();
        return primary switch
        {
            "zh" or "yue" or "cmn" or "hak" or "nan" or "wuu" => Chinese,
            "en" => English,
            _ => English
        };
    }
}

/// <summary>Static accessors for XAML x:Static bindings. Re-evaluated when the window is rebuilt.</summary>
public static class Strings
{
    public static string AppTitle => Loc.T("AppTitle");
    public static string PaneTitle => Loc.T("PaneTitle");
    public static string QuickAccess => Loc.T("QuickAccess");
    public static string Drives => Loc.T("Drives");
    public static string NewTab => Loc.T("NewTab");
    public static string CloseTab => Loc.T("CloseTab");
    public static string NextTab => Loc.T("NextTab");
    public static string PreviousTab => Loc.T("PreviousTab");
    public static string ReopenClosedTab => Loc.T("ReopenClosedTab");
    public static string CloseOtherTabs => Loc.T("CloseOtherTabs");
    public static string CloseTabsToRight => Loc.T("CloseTabsToRight");
    public static string DuplicateTab => Loc.T("DuplicateTab");
    public static string MoveTabLeft => Loc.T("MoveTabLeft");
    public static string MoveTabRight => Loc.T("MoveTabRight");
    public static string TabsMenu => Loc.T("TabsMenu");
    public static string NoClosedTabs => Loc.T("NoClosedTabs");
    public static string SwitchLanguage => Loc.T("SwitchLanguage");
    public static string BuildChannel => Loc.T("BuildChannel");
    public static string BuildVersion => Loc.T("BuildVersion");
    public static string BuildChannelDev => Loc.T("BuildChannel_dev");
    public static string BuildChannelStable => Loc.T("BuildChannel_stable");
    public static string BuildChannelNightly => Loc.T("BuildChannel_nightly");
    public static string BuildChannelPreview => Loc.T("BuildChannel_preview");
    public static string DetailsPane => Loc.T("DetailsPane");
    public static string ToggleDetailsPane => Loc.T("ToggleDetailsPane");
    public static string NoSelection => Loc.T("NoSelection");
    public static string Back => Loc.T("Back");
    public static string Forward => Loc.T("Forward");
    public static string Up => Loc.T("Up");
    public static string Refresh => Loc.T("Refresh");
    public static string NewFolder => Loc.T("NewFolder");
    public static string Copy => Loc.T("Copy");
    public static string Cut => Loc.T("Cut");
    public static string Paste => Loc.T("Paste");
    public static string Rename => Loc.T("Rename");
    public static string Delete => Loc.T("Delete");
    public static string ToggleHidden => Loc.T("ToggleHidden");
    public static string ToggleView => Loc.T("ToggleView");
    public static string ToggleTheme => Loc.T("ToggleTheme");
    public static string Sort => Loc.T("Sort");
    public static string Layout => Loc.T("Layout");
    public static string EditAddress => Loc.T("EditAddress");
    public static string DeepSearch => Loc.T("DeepSearch");
    public static string AddressPlaceholder => Loc.T("AddressPlaceholder");
    public static string SearchPlaceholder => Loc.T("SearchPlaceholder");
    public static string ColName => Loc.T("ColName");
    public static string ColSize => Loc.T("ColSize");
    public static string ColKind => Loc.T("ColKind");
    public static string ColModified => Loc.T("ColModified");
    public static string EmptyFolder => Loc.T("EmptyFolder");
    public static string CtxOpen => Loc.T("CtxOpen");
    public static string CtxReveal => Loc.T("CtxReveal");
    public static string CtxCopy => Loc.T("CtxCopy");
    public static string CtxCut => Loc.T("CtxCut");
    public static string CtxPaste => Loc.T("CtxPaste");
    public static string CtxCopyPath => Loc.T("CtxCopyPath");
    public static string CtxNewFolder => Loc.T("CtxNewFolder");
    public static string CtxRename => Loc.T("CtxRename");
    public static string CtxDelete => Loc.T("CtxDelete");
    public static string CtxSelectAll => Loc.T("CtxSelectAll");
    public static string CtxProperties => Loc.T("CtxProperties");
    public static string DlgPlaceholder => Loc.T("DlgPlaceholder");
    public static string DlgCancel => Loc.T("DlgCancel");
    public static string DlgOK => Loc.T("DlgOK");
    public static string DlgClose => Loc.T("DlgClose");
    public static string DlgNewFolderTitle => Loc.T("DlgNewFolderTitle");
    public static string DlgNewFolderMessage => Loc.T("DlgNewFolderMessage");
    public static string DlgNewFolderDefault => Loc.T("DlgNewFolderDefault");
    public static string DlgCreate => Loc.T("DlgCreate");
    public static string DlgRenameTitle => Loc.T("DlgRenameTitle");
    public static string DlgRenameMessage => Loc.T("DlgRenameMessage");
    public static string DlgRenameButton => Loc.T("DlgRenameButton");
    public static string DlgDeleteTitle => Loc.T("DlgDeleteTitle");
    public static string DlgDeleteMessage => Loc.T("DlgDeleteMessage");
    public static string DlgPropertiesTitle => Loc.T("DlgPropertiesTitle");
    public static string MsgInvalidPath => Loc.T("MsgInvalidPath");
    public static string MsgCannotOpenLocation => Loc.T("MsgCannotOpenLocation");
    public static string MsgLocationDetail => Loc.T("MsgLocationDetail");
    public static string MsgReadFailed => Loc.T("MsgReadFailed");
    public static string MsgSearch => Loc.T("MsgSearch");
    public static string MsgSearchEmpty => Loc.T("MsgSearchEmpty");
    public static string MsgSearchBreadcrumb => Loc.T("MsgSearchBreadcrumb");
    public static string MsgSearchDone => Loc.T("MsgSearchDone");
    public static string MsgSearchDoneBody => Loc.T("MsgSearchDoneBody");
    public static string MsgSearchFailed => Loc.T("MsgSearchFailed");
    public static string MsgCannotOpen => Loc.T("MsgCannotOpen");
    public static string MsgCreatedFolder => Loc.T("MsgCreatedFolder");
    public static string MsgCreateFailed => Loc.T("MsgCreateFailed");
    public static string MsgRenameDone => Loc.T("MsgRenameDone");
    public static string MsgRenameFailed => Loc.T("MsgRenameFailed");
    public static string MsgMovedToTrash => Loc.T("MsgMovedToTrash");
    public static string MsgDeleteFailed => Loc.T("MsgDeleteFailed");
    public static string MsgCopied => Loc.T("MsgCopied");
    public static string MsgCut => Loc.T("MsgCut");
    public static string MsgClipboardHint => Loc.T("MsgClipboardHint");
    public static string MsgPasteDone => Loc.T("MsgPasteDone");
    public static string MsgPasteBody => Loc.T("MsgPasteBody");
    public static string MsgCopiedWord => Loc.T("MsgCopiedWord");
    public static string MsgMovedWord => Loc.T("MsgMovedWord");
    public static string MsgPasteFailed => Loc.T("MsgPasteFailed");
    public static string MsgPathCopied => Loc.T("MsgPathCopied");
    public static string MsgCannotReveal => Loc.T("MsgCannotReveal");
    public static string MsgOperationFailed => Loc.T("MsgOperationFailed");
    public static string PropFolder => Loc.T("PropFolder");
    public static string PropFile => Loc.T("PropFile");
    public static string Unknown => Loc.T("Unknown");
    public static string SortName => Loc.T("SortName");
    public static string SortSize => Loc.T("SortSize");
    public static string SortKind => Loc.T("SortKind");
    public static string SortModified => Loc.T("SortModified");
    public static string SortAsc => Loc.T("SortAsc");
    public static string SortDesc => Loc.T("SortDesc");
    public static string ThemeSystem => Loc.T("ThemeSystem");
    public static string ThemeLight => Loc.T("ThemeLight");
    public static string ThemeDark => Loc.T("ThemeDark");
    public static string StatusPreparing => Loc.T("StatusPreparing");
    public static string StatusItems => Loc.T("StatusItems");
    public static string StatusSearchResults => Loc.T("StatusSearchResults");
    public static string StatusFiltered => Loc.T("StatusFiltered");
    public static string SelectionText => Loc.T("SelectionText");
    public static string KindFolder => Loc.T("KindFolder");
    public static string KindFile => Loc.T("KindFile");
    public static string KindExtension => Loc.T("KindExtension");
    public static string LocHome => Loc.T("LocHome");
    public static string LocDesktop => Loc.T("LocDesktop");
    public static string LocDownloads => Loc.T("LocDownloads");
    public static string LocDocuments => Loc.T("LocDocuments");
    public static string LocPictures => Loc.T("LocPictures");
    public static string LocVideos => Loc.T("LocVideos");
    public static string PaneName => Loc.T("PaneName");
    public static string PaneType => Loc.T("PaneType");
    public static string PaneSize => Loc.T("PaneSize");
    public static string PaneModified => Loc.T("PaneModified");
    public static string PanePath => Loc.T("PanePath");
    public static string PaneContains => Loc.T("PaneContains");
    public static string TabHome => Loc.T("TabHome");
    public static string PlatformText => Loc.T("PlatformText");
    public static string LblNew => Loc.T("LblNew");
    public static string LblCut => Loc.T("LblCut");
    public static string LblCopy => Loc.T("LblCopy");
    public static string LblPaste => Loc.T("LblPaste");
    public static string LblRename => Loc.T("LblRename");
    public static string LblDelete => Loc.T("LblDelete");
    public static string LblSort => Loc.T("LblSort");
    public static string LblLayout => Loc.T("LblLayout");
    public static string LblDetails => Loc.T("LblDetails");
    public static string LblMore => Loc.T("LblMore");
    public static string LblHidden => Loc.T("LblHidden");
    public static string LblSettings => Loc.T("LblSettings");
    public static string LblPreview => Loc.T("LblPreview");
    public static string LblOpen => Loc.T("LblOpen");
    public static string LblProperties => Loc.T("LblProperties");
    public static string LblToggleSidebar => Loc.T("LblToggleSidebar");
    public static string LblStatusCenter => Loc.T("LblStatusCenter");
    public static string LblNoPreview => Loc.T("LblNoPreview");
    public static string LblView => Loc.T("LblView");
    public static string LblSelect => Loc.T("LblSelect");
    public static string MsgDropDone => Loc.T("MsgDropDone");
    public static string MsgDropCopied => Loc.T("MsgDropCopied");
    public static string MsgDropBody => Loc.T("MsgDropBody");
    public static string MsgDropFailed => Loc.T("MsgDropFailed");
    public static string MsgDropInvalid => Loc.T("MsgDropInvalid");
    public static string MsgWatcherChanged => Loc.T("MsgWatcherChanged");
    public static string CodeTab => Loc.T("CodeTab");
    public static string OfficeTab => Loc.T("OfficeTab");
    public static string OfficeWord => Loc.T("OfficeWord");
    public static string OfficeExcel => Loc.T("OfficeExcel");
    public static string OfficePowerPoint => Loc.T("OfficePowerPoint");
    public static string OfficeTabFile => Loc.T("OfficeTabFile");
    public static string OfficeTabHome => Loc.T("OfficeTabHome");
    public static string OfficeTabInsert => Loc.T("OfficeTabInsert");
    public static string OfficeTabView => Loc.T("OfficeTabView");
    public static string OfficeGroupFile => Loc.T("OfficeGroupFile");
    public static string OfficeGroupFont => Loc.T("OfficeGroupFont");
    public static string OfficeGroupCells => Loc.T("OfficeGroupCells");
    public static string OfficeGroupSlides => Loc.T("OfficeGroupSlides");
    public static string OfficeNew => Loc.T("OfficeNew");
    public static string OfficeNewHint => Loc.T("OfficeNewHint");
    public static string OfficeNewNameHint => Loc.T("OfficeNewNameHint");
    public static string OfficeSave => Loc.T("OfficeSave");
    public static string OfficeBold => Loc.T("OfficeBold");
    public static string OfficeItalic => Loc.T("OfficeItalic");
    public static string OfficeUnderline => Loc.T("OfficeUnderline");
    public static string OfficeOpened => Loc.T("OfficeOpened");
    public static string OfficeSaved => Loc.T("OfficeSaved");
    public static string OfficeOpenFailed => Loc.T("OfficeOpenFailed");
    public static string OfficeSaveFailed => Loc.T("OfficeSaveFailed");
    public static string OfficeCreateFailed => Loc.T("OfficeCreateFailed");
    public static string OfficeEmpty => Loc.T("OfficeEmpty");
    public static string OfficeEmptyHint => Loc.T("OfficeEmptyHint");
    public static string OfficeExcelHint => Loc.T("OfficeExcelHint");
    public static string OfficeReadOnlyHint => Loc.T("OfficeReadOnlyHint");
    public static string OfficeDisclaimer => Loc.T("OfficeDisclaimer");

    public static string ExtensionsTab => Loc.T("ExtensionsTab");
    public static string ClosePage => Loc.T("ClosePage");
    public static string CodeTitle => Loc.T("CodeTitle");
    public static string CodeOpenFile => Loc.T("CodeOpenFile");
    public static string CodeSave => Loc.T("CodeSave");
    public static string CodeLanguage => Loc.T("CodeLanguage");
    public static string CodeEncoding => Loc.T("CodeEncoding");
    public static string CodeTheme => Loc.T("CodeTheme");
    public static string CodeEngineLoading => Loc.T("CodeEngineLoading");
    public static string CodeNoFile => Loc.T("CodeNoFile");
    public static string CodeCloseHint => Loc.T("CodeCloseHint");
    public static string ExtTitle => Loc.T("ExtTitle");
    public static string ExtSearchPlaceholder => Loc.T("ExtSearchPlaceholder");
    public static string ExtEnable => Loc.T("ExtEnable");
    public static string ExtDisable => Loc.T("ExtDisable");
    public static string ExtOpenFolder => Loc.T("ExtOpenFolder");
    public static string ExtRefresh => Loc.T("ExtRefresh");
    public static string ExtSelectHint => Loc.T("ExtSelectHint");
    public static string ExtNotice => Loc.T("ExtNotice");
    public static string MktInstalledTab => Loc.T("MktInstalledTab");
    public static string MktMarketplaceTab => Loc.T("MktMarketplaceTab");
    public static string MktSearchPlaceholder => Loc.T("MktSearchPlaceholder");
    public static string MktSearch => Loc.T("MktSearch");
    public static string MktUninstall => Loc.T("MktUninstall");
    public static string MktNotice => Loc.T("MktNotice");
    public static string MktOpenStore => Loc.T("MktOpenStore");
    public static string MktLicense => Loc.T("MktLicense");
}
