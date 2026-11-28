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
        ["AddressUrlHint"] = new[] { "This is a URL - Enter opens it in FileHub", "这是网址，回车交给 FileHub 打开" },
        ["AddressPlaceholder"] = new[] { "Path or URL, then Enter (~ and https:// supported)", "输入路径或网址后回车，支持 ~ 与 https://" },
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
        ["WinMinimize"] = new[] { "Minimize", "最小化" },
        ["WinMaximize"] = new[] { "Maximize", "最大化" },
        ["WinClose"] = new[] { "Close window", "关闭窗口" },
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
        ["OfficeTab"] = new[] { "Office", "Office" },
        ["OfficeBackToFiles"] = new[] { "Files", "文件" },
        ["OfficeDocumentModeHint"] = new[] { "Document mode - file manager panels are hidden while a document is open", "文档模式 - 打开文档时自动隐藏文件管理器界面" },
        ["OfficeUnsavedTitle"] = new[] { "Unsaved changes", "未保存的修改" },        ["OfficeUnsavedSwitch"] = new[] { "The current document has unsaved changes. Open another document and lose them?", "当前文档有未保存的修改，打开其他文档会丢失这些修改。继续？" },
        ["OfficeUnsavedClose"] = new[] { "The current document has unsaved changes. Leave document mode and lose them?", "当前文档有未保存的修改，返回文件列表会丢失这些修改。继续？" },
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
        ["OfficeOpenFailedTitle"] = new[] { "Cannot open document", "无法打开文档" },
        ["OfficeSaveFailed"] = new[] { "Cannot save: {0}", "无法保存：{0}" },
        ["OfficeCreateFailed"] = new[] { "Cannot create document: {0}", "无法创建文档：{0}" },
        ["OfficeEmpty"] = new[] { "No document open", "未打开文档" },
        ["OfficeEmptyHint"] = new[] { "Double-click a Word, Excel or PowerPoint file in FileHub to open it here", "在 FileHub 中双击 Word、Excel 或 PowerPoint 文件即可在此打开" },
        ["OfficeExcelHint"] = new[] { "Click a cell to edit its value. Save writes changes back to the workbook.", "点击单元格编辑内容，保存后写回工作簿。" },
        ["OfficeReadOnlyHint"] = new[] { "Slide outline view - editing is not supported", "幻灯片大纲视图，不支持编辑" },
        ["OfficeDisclaimer"] = new[] { "Open XML editing - complex layouts may be simplified", "基于 Open XML 的编辑 - 复杂版式可能被简化" },
        ["OfficeAdvancedPanel"] = new[] { "Advanced", "高级功能" },
        ["OfficeMultilevel"] = new[] { "Multilevel list", "多级列表" },
        ["OfficeRevisions"] = new[] { "Tracking", "修订" },
        ["OfficeTrackChanges"] = new[] { "Track changes", "修订模式" },
        ["OfficeAccept"] = new[] { "Accept", "接受" },
        ["OfficeReject"] = new[] { "Reject", "拒绝" },
        ["OfficeAcceptAll"] = new[] { "Accept all", "全部接受" },
        ["OfficeRejectAll"] = new[] { "Reject all", "全部拒绝" },
        ["OfficeCharts"] = new[] { "Charts", "图表" },
        ["OfficeChartColumn"] = new[] { "Column", "柱形图" },
        ["OfficeChartBar"] = new[] { "Bar", "条形图" },
        ["OfficeChartLine"] = new[] { "Line", "折线图" },
        ["OfficeChartPie"] = new[] { "Pie", "饼图" },
        ["OfficeChartArea"] = new[] { "Area", "面积图" },
        ["OfficePivotTables"] = new[] { "Pivot", "数据透视表" },
        ["OfficePivotCreate"] = new[] { "Create pivot", "创建透视表" },
        ["OfficePivotRefresh"] = new[] { "Refresh", "刷新" },
        ["OfficeChartNoData"] = new[] { "Enter some cell values first", "请先输入单元格数据" },
        ["OfficeChartCreated"] = new[] { "Created chart {0}", "已创建图表 {0}" },
        ["OfficePivotCreated"] = new[] { "Created pivot {0}", "已创建透视表 {0}" },
        ["OfficeTrackingOn"] = new[] { "Track changes is on", "修订模式已开启" },
        ["OfficeTrackingOff"] = new[] { "Track changes is off", "修订模式已关闭" },
        ["OfficeObjects"] = new[] { "Embedded objects", "嵌入对象" },
        ["OfficeMacroRecord"] = new[] { "Record", "录制" },
        ["OfficeMacroStop"] = new[] { "Stop", "停止" },
        ["OfficeMacroSaved"] = new[] { "Saved {0}", "已保存 {0}" },
        ["OfficeMacroRan"] = new[] { "Ran {0}", "已运行 {0}" },
        ["OfficeMacroRecording"] = new[] { "Recording editor actions", "正在录制编辑操作" },
        ["OfficeVba"] = new[] { "VBA project", "VBA 项目" },
        ["OfficeMacroSecurity"] = new[] { "Macro security", "宏安全级别" },
        ["OfficeVbaModules"] = new[] { "VBA modules (read only)", "VBA 模块（只读）" },
        ["OfficeRevisionsTab"] = new[] { "Revisions", "修订记录" },
        ["OfficeNumberingTab"] = new[] { "Numbering", "编号定义" },
        ["OfficeChartsTab"] = new[] { "Charts", "图表" },
        ["OfficePivotsTab"] = new[] { "Pivots", "透视表" },
        ["OfficeMacrosTab"] = new[] { "Macros", "宏" },
        ["OfficeObjectsTab"] = new[] { "Objects", "对象" },
        ["OfficeExtract"] = new[] { "Extract", "提取" },
        ["OfficeNoRevisions"] = new[] { "No tracked changes", "没有修订记录" },
        ["OfficeNoCharts"] = new[] { "No charts yet", "暂无图表" },
        ["OfficeNoPivots"] = new[] { "No pivot tables yet", "暂无数据透视表" },
        ["OfficeNoObjects"] = new[] { "No embedded objects", "没有嵌入对象" },
        ["OfficeNoModules"] = new[] { "No VBA modules", "没有 VBA 模块" },
        ["OfficeNoMacros"] = new[] { "No recorded macros", "没有录制的宏" },
        ["OfficeSource"] = new[] { "Source", "来源" },
        ["OfficeAuthor"] = new[] { "Author", "作者" },
        ["OfficeValue"] = new[] { "Value", "值" },
        ["OfficeLevel"] = new[] { "Level", "级别" },
        ["OfficeRunMacro"] = new[] { "Run", "运行" },
        ["OfficeDeleteMacro"] = new[] { "Delete", "删除" },
        ["OfficeWpsDecoded"] = new[] { "Decoded from {0} - choose Save As to keep an OOXML copy", "已解码 {0} - 请另存为以保留 OOXML 副本" },
        ["OfficeRichDisclaimer"] = new[] { "OOXML advanced features: VBA source is inspected but never executed; macro recording is a FileHub-native action replay.", "OOXML 高级特性：VBA 源码仅解析展示，从不执行；宏录制是 FileHub 原生的操作回放。" },
        ["OfficeSaveAs"] = new[] { "Save as", "另存为" },
        ["OfficeExportHtml"] = new[] { "Export HTML", "导出 HTML" },
        ["OfficeUndo"] = new[] { "Undo", "撤销" },
        ["OfficeRedo"] = new[] { "Redo", "重做" },
        ["OfficeFind"] = new[] { "Find", "查找" },
        ["OfficeReplace"] = new[] { "Replace", "替换" },
        ["OfficeFindCount"] = new[] { "{0} match(es)", "匹配 {0} 处" },
        ["OfficeReplaceCount"] = new[] { "Replaced {0} occurrence(s)", "已替换 {0} 处" },
        ["OfficeStyle"] = new[] { "Style", "样式" },
        ["OfficeFormulaHint"] = new[] { "Enter a value or a formula, e.g. =SUM(A1:A5)", "输入值或公式，例如 =SUM(A1:A5)" },
        ["OfficeLayout"] = new[] { "Layout", "版式" },
        ["OfficeNotes"] = new[] { "Notes", "备注" },
        ["OfficeNotesHint"] = new[] { "Speaker notes for this slide", "本页演讲者备注" },
        ["OfficeGroupParagraph"] = new[] { "Paragraph", "段落" },
        ["OfficeAlignLeft"] = new[] { "Left", "左对齐" },
        ["OfficeAlignCenter"] = new[] { "Center", "居中" },
        ["OfficeAlignRight"] = new[] { "Right", "右对齐" },
        ["OfficeAlignJustify"] = new[] { "Justify", "两端对齐" },
        ["OfficeBullets"] = new[] { "Bullets", "项目符号" },
        ["OfficeNumbering"] = new[] { "Numbering", "编号" },
        ["OfficeNoList"] = new[] { "No list", "无列表" },
        ["OfficeAddParagraph"] = new[] { "Paragraph", "段落" },
        ["OfficeDeleteParagraph"] = new[] { "Delete", "删除" },
        ["OfficeAddTable"] = new[] { "Table", "表格" },
        ["OfficeInsertImage"] = new[] { "Picture", "图片" },
        ["OfficeInsertLink"] = new[] { "Link", "链接" },
        ["OfficeInsertLinkHint"] = new[] { "Link address", "链接地址" },
        ["OfficeInsert"] = new[] { "Insert", "插入" },
        ["OfficeTabLayout"] = new[] { "Layout", "布局" },
        ["OfficeTabReview"] = new[] { "Review", "审阅" },
        ["OfficeTabFormulas"] = new[] { "Formulas", "公式" },
        ["OfficeTabData"] = new[] { "Data", "数据" },
        ["OfficeTabDesign"] = new[] { "Design", "设计" },
        ["OfficeInsertRow"] = new[] { "Insert row", "插入行" },
        ["OfficeDeleteRow"] = new[] { "Delete row", "删除行" },
        ["OfficeInsertColumn"] = new[] { "Insert column", "插入列" },
        ["OfficeDeleteColumn"] = new[] { "Delete column", "删除列" },
        ["OfficeRecalculate"] = new[] { "Calculate", "重新计算" },
        ["OfficeAddSheet"] = new[] { "New sheet", "新建工作表" },
        ["OfficeRenameSheet"] = new[] { "Rename", "重命名" },
        ["OfficeDeleteSheet"] = new[] { "Delete sheet", "删除工作表" },
        ["OfficeDeleteSheetHint"] = new[] { "Delete sheet {0}?", "删除工作表 {0} ？" },
        ["OfficeSheetNameHint"] = new[] { "Sheet name", "工作表名称" },
        ["OfficeAddSlide"] = new[] { "New slide", "新建幻灯片" },
        ["OfficeDuplicateSlide"] = new[] { "Duplicate", "复制" },
        ["OfficeDeleteSlide"] = new[] { "Delete slide", "删除幻灯片" },
        ["OfficeAddTextBox"] = new[] { "Text box", "文本框" },
        ["OfficeLayoutTitle"] = new[] { "Title only", "仅标题" },
        ["OfficeLayoutBlank"] = new[] { "Blank", "空白" },
        ["OfficeSum"] = new[] { "Sum", "求和" },
        ["OfficeAverage"] = new[] { "Average", "平均值" },
        ["OfficeCount"] = new[] { "Count", "计数" },
        ["OfficeSavedFile"] = new[] { "Saved {0}", "已保存 {0}" },
        ["OfficeExported"] = new[] { "Exported to {0}", "已导出到 {0}" },
        ["OfficeClickTitle"] = new[] { "Click to add title", "单击添加标题" },
        ["OfficeClickText"] = new[] { "Click to add text", "单击添加文本" },

        ["ExtensionsTab"] = new[] { "Extensions", "扩展" },
        ["SettingsSubtitle"] = new[] { "Preferences for this window and the built-in tools", "本窗口与内置工具的偏好设置" },
        ["SettingsNavGeneral"] = new[] { "General", "常规" },
        ["SettingsNavAppearance"] = new[] { "Appearance", "外观" },
        ["SettingsNavAbout"] = new[] { "About", "关于" },
        ["SettingsViewSection"] = new[] { "View", "视图" },
        ["SettingsShowHidden"] = new[] { "Show hidden files", "显示隐藏文件" },
        ["SettingsShowHiddenHint"] = new[] { "Files and folders starting with a dot on macOS and Linux.", "在 macOS 与 Linux 上以点开头的文件与文件夹。" },
        ["SettingsSidebar"] = new[] { "Sidebar", "侧栏" },
        ["SettingsSidebarHint"] = new[] { "The file browser column on the left.", "左侧的文件浏览器栏。" },
        ["SettingsDetailsPane"] = new[] { "Details pane", "详细信息窗格" },
        ["SettingsDetailsPaneHint"] = new[] { "Preview and metadata panel on the right.", "右侧的预览与元数据面板。" },
        ["SettingsDualPaneHint"] = new[] { "Browse two folders side by side and drag between them.", "并排浏览两个文件夹，可在两者之间拖动。" },
        ["SettingsLanguageSection"] = new[] { "Language", "语言" },
        ["SettingsLanguage"] = new[] { "Interface language", "界面语言" },
        ["SettingsAppearanceTheme"] = new[] { "Theme", "主题" },
        ["SettingsThemeHint"] = new[] { "System follows the light/dark setting of the operating system. Changes apply immediately.", "「跟随系统」会使用操作系统的浅色/深色设置。修改立即生效。" },
        ["SettingsExtensionsHint"] = new[] { "VS Code extension packages installed for the built-in code editor. Extension JavaScript is never executed.", "安装给内置代码编辑器的 VS Code 扩展包。扩展自带的 JavaScript 不会被执行。" },
        ["SettingsAboutVersion"] = new[] { "Version", "版本" },
        ["SettingsAboutBuild"] = new[] { "Build", "构建" },
        ["SettingsAboutLicense"] = new[] { "License", "许可" },
        ["SettingsAboutBody"] = new[] { "A file manager with a built-in Office suite, code editor and archive/remote browsing. Bundled third-party components keep their own licenses; see THIRD-PARTY-NOTICES.md.", "带内置 Office 套件、代码编辑器与压缩包/远程浏览的文件管理器。内置第三方组件保留各自许可，详见 THIRD-PARTY-NOTICES.md。" },
        ["SettingsOpenProject"] = new[] { "Project page", "项目主页" },
        ["SettingsOpenNotices"] = new[] { "Third-party notices", "第三方声明" },
        ["SettingsOpenLicense"] = new[] { "License file", "许可文件" },
        ["SettingsNavFiles"] = new[] { "File manager", "文件管理器" },
        ["SettingsNavCode"] = new[] { "Code editor", "代码编辑器" },
        ["SettingsNavOffice"] = new[] { "Office", "Office" },
        ["SettingsGeneralHint"] = new[] { "Interface language, theme and window layout. Everything applies immediately.", "界面语言、主题与窗口布局，修改立即生效。" },
        ["SettingsWindowSection"] = new[] { "Window", "窗口" },
        ["SettingsThemeSection"] = new[] { "Theme", "主题" },
        ["SettingsFilesHint"] = new[] { "List behaviour and the defaults every new tab starts with.", "列表行为，同时作为每个新标签页的默认值。" },
        ["SettingsFilesListSection"] = new[] { "List", "列表" },
        ["SettingsShowExtensions"] = new[] { "Show file extensions", "显示文件扩展名" },
        ["SettingsShowExtensionsHint"] = new[] { "When off, the name column hides the extension; rename and copy path still use the real name.", "关闭后名称列不再显示扩展名；重命名与复制路径仍使用真实文件名。" },
        ["SettingsFoldersFirst"] = new[] { "Folders before files", "文件夹排在文件前面" },
        ["SettingsFoldersFirstHint"] = new[] { "Groups directories at the top regardless of the sort column.", "无论按哪一列排序，目录都集中在最前面。" },
        ["SettingsThumbnails"] = new[] { "Image thumbnails", "图片缩略图" },
        ["SettingsThumbnailsHint"] = new[] { "Generates previews for the first screenful of images. Turning it off keeps large photo folders light.", "为首屏图片生成预览。关闭后打开大量照片的目录更轻快。" },
        ["SettingsFilesSortSection"] = new[] { "Sorting and view", "排序与视图" },
        ["SettingsDefaultView"] = new[] { "Icon view by default", "默认使用图标视图" },
        ["SettingsDefaultViewHint"] = new[] { "New tabs open as a tile grid instead of the details list.", "新标签页以图标网格打开，而不是详细信息列表。" },
        ["SettingsSortDefault"] = new[] { "Default sort column", "默认排序列" },
        ["SettingsSortDefaultHint"] = new[] { "The column a new tab sorts by. Clicking a column header in the list overrides it for that tab.", "新标签页使用的排序列；在列表里点列头只影响当前标签页。" },
        ["SettingsSortDescending"] = new[] { "Descending", "降序" },
        ["SettingsFilesDeleteSection"] = new[] { "Delete", "删除" },
        ["SettingsConfirmDelete"] = new[] { "Confirm before deleting", "删除前确认" },
        ["SettingsConfirmDeleteHint"] = new[] { "Items go to the recycle bin / trash. Turn this off only if the second safety net is not wanted.", "文件会进入回收站 / 废纸篓。关闭后删除不再二次确认。" },
        ["SettingsCodeHint"] = new[] { "These map one-to-one to VS Code editor options and are pushed to Monaco immediately.", "与 VS Code 的 editor.* 选项一一对应，修改会立即推送到 Monaco。" },
        ["SettingsCodeFontSection"] = new[] { "Font", "字体" },
        ["SettingsCodeFontFamily"] = new[] { "Font family", "字体族" },
        ["SettingsCodeFontFamilyHint"] = new[] { "CSS font list, e.g. \"JetBrains Mono, Menlo, monospace\".", "CSS 字体列表，例如「JetBrains Mono, Menlo, monospace」。" },
        ["SettingsCodeFontSize"] = new[] { "Font size", "字号" },
        ["SettingsCodeFontSizeHint"] = new[] { "Editor font size in pixels.", "编辑器字号（像素）。" },
        ["SettingsCodeIndentSection"] = new[] { "Indentation", "缩进" },
        ["SettingsCodeTabSize"] = new[] { "Tab size", "Tab 宽度" },
        ["SettingsCodeTabSizeHint"] = new[] { "Number of columns a tab occupies.", "一个 Tab 占用的列数。" },
        ["SettingsCodeInsertSpaces"] = new[] { "Insert spaces", "插入空格" },
        ["SettingsCodeInsertSpacesHint"] = new[] { "Press Tab to insert spaces instead of a tab character.", "按 Tab 时插入空格而不是制表符。" },
        ["SettingsCodeViewSection"] = new[] { "View", "显示" },
        ["SettingsCodeWordWrap"] = new[] { "Word wrap", "自动换行" },
        ["SettingsCodeWordWrapHint"] = new[] { "Off keeps long lines scrollable; On wraps at the viewport; Column wraps at the configured column.", "关闭时长行横向滚动；「开」按窗口宽度换行；「按列」在设定列宽处换行。" },
        ["SettingsCodeLineNumbers"] = new[] { "Line numbers", "行号" },
        ["SettingsCodeLineNumbersHint"] = new[] { "Relative shows the distance from the cursor line.", "「相对」显示与光标行的距离。" },
        ["SettingsCodeWhitespace"] = new[] { "Render whitespace", "显示空白字符" },
        ["SettingsCodeWhitespaceHint"] = new[] { "Which whitespace characters are drawn as dots and arrows.", "哪些空白字符以点和箭头显示。" },
        ["SettingsCodeMinimap"] = new[] { "Minimap", "小地图" },
        ["SettingsCodeMinimapHint"] = new[] { "Code overview column on the right edge.", "右侧的代码缩略图列。" },
        ["SettingsCodeBrackets"] = new[] { "Bracket pair colorization", "括号对着色" },
        ["SettingsCodeBracketsHint"] = new[] { "Colours matching brackets and guides.", "为配对的括号及其引导线着色。" },
        ["SettingsCodeCursor"] = new[] { "Cursor style", "光标样式" },
        ["SettingsCodeCursorHint"] = new[] { "Shape of the insertion caret.", "插入光标的形状。" },
        ["SettingsCodeSmooth"] = new[] { "Smooth scrolling", "平滑滚动" },
        ["SettingsCodeSmoothHint"] = new[] { "Animate scrolling instead of jumping line by line.", "滚动时使用动画，而不是按行跳动。" },
        ["SettingsCodeSaveSection"] = new[] { "Saving and theme", "保存与主题" },
        ["SettingsCodeAutoSave"] = new[] { "Auto save", "自动保存" },
        ["SettingsCodeAutoSaveHint"] = new[] { "Delay saves shortly after typing stops; Off keeps Ctrl+S as the only writer.", "「延迟」在停止输入一小段时间后写入；「关闭」时只有 Ctrl+S 会写文件。" },
        ["SettingsCodeAutoSaveDelay"] = new[] { "Auto save delay (ms)", "自动保存延迟（毫秒）" },
        ["SettingsCodeAutoSaveDelayHint"] = new[] { "How long typing must pause before the file is written.", "停止输入多久后写入文件。" },
        ["SettingsCodeTheme"] = new[] { "Editor theme", "编辑器主题" },
        ["SettingsCodeThemeHint"] = new[] { "Themes contributed by enabled VS Code extensions; Default follows Monaco's built-in theme.", "由已启用扩展提供的主题；「默认」使用 Monaco 内置主题。" },
        ["SettingsWrapOff"] = new[] { "Off", "关闭" },
        ["SettingsWrapOn"] = new[] { "On", "开" },
        ["SettingsWrapColumn"] = new[] { "At column", "按列宽" },
        ["SettingsLineNumbersOn"] = new[] { "On", "开" },
        ["SettingsLineNumbersRelative"] = new[] { "Relative", "相对" },
        ["SettingsLineNumbersOff"] = new[] { "Off", "关闭" },
        ["SettingsWhitespaceNone"] = new[] { "None", "不显示" },
        ["SettingsWhitespaceBoundary"] = new[] { "Boundary", "仅边界" },
        ["SettingsWhitespaceSelection"] = new[] { "Selection", "仅选中" },
        ["SettingsWhitespaceAll"] = new[] { "All", "全部" },
        ["SettingsCursorLine"] = new[] { "Line", "竖线" },
        ["SettingsCursorBlock"] = new[] { "Block", "方块" },
        ["SettingsCursorUnderline"] = new[] { "Underline", "下划线" },
        ["SettingsAutoSaveOff"] = new[] { "Off", "关闭" },
        ["SettingsAutoSaveDelay"] = new[] { "After a delay", "延迟保存" },
        ["SettingsOfficeHint"] = new[] { "Document canvas, defaults for new documents and auto save. Values marked as defaults only apply to documents created later.", "文档画布、新文档默认值与自动保存。标注「默认」的项目只影响之后新建的文档。" },
        ["SettingsOfficeDocSection"] = new[] { "New documents", "新建文档" },
        ["SettingsOfficeDefaultKind"] = new[] { "Default document type", "默认文档类型" },
        ["SettingsOfficeDefaultKindHint"] = new[] { "Preselected type in the Office page's New box.", "Office 页「新建」框里预选的文件类型。" },
        ["SettingsOfficeFontSize"] = new[] { "Body font size (pt)", "正文字号（磅）" },
        ["SettingsOfficeFontSizeHint"] = new[] { "Applied to new paragraphs and to the body of a freshly created document.", "用于新段落以及新建文档的正文。" },
        ["SettingsOfficeZoom"] = new[] { "Default zoom (%)", "默认缩放（%）" },
        ["SettingsOfficeZoomHint"] = new[] { "Zoom level restored whenever a document opens.", "每次打开文档时使用的缩放比例。" },
        ["SettingsOfficeViewSection"] = new[] { "Canvas", "画布" },
        ["SettingsOfficeRuler"] = new[] { "Show ruler", "显示标尺" },
        ["SettingsOfficeRulerHint"] = new[] { "Inch ruler above the Word page.", "Word 页面上方的英寸标尺。" },
        ["SettingsOfficeWidePage"] = new[] { "Wide page", "宽页视图" },
        ["SettingsOfficeWidePageHint"] = new[] { "Uses the full window width instead of an 860px paper column.", "使用整个窗口宽度，而不是 860 像素的纸张栏。" },
        ["SettingsOfficeGridLines"] = new[] { "Cell grid lines", "单元格网格线" },
        ["SettingsOfficeGridLinesHint"] = new[] { "Borders between worksheet cells; formulas and fills are unaffected.", "工作表单元格之间的边框；不影响公式与填充色。" },
        ["SettingsOfficeTrackChanges"] = new[] { "Track changes by default", "默认开启修订" },
        ["SettingsOfficeTrackChangesHint"] = new[] { "New documents record revisions; existing files keep their own flag.", "新建文档会记录修订；已有文件保留自己的设置。" },
        ["SettingsOfficeSaveSection"] = new[] { "Saving and security", "保存与安全" },
        ["SettingsOfficeAutoSave"] = new[] { "Auto save", "自动保存" },
        ["SettingsOfficeAutoSaveHint"] = new[] { "Writes local documents on the interval below while they have unsaved edits. Decoded WPS files are never auto-saved.", "本地文档有未保存修改时按下面的间隔写入。解码得到的 WPS 文档不会自动保存。" },
        ["SettingsOfficeAutoSaveInterval"] = new[] { "Auto save interval (s)", "自动保存间隔（秒）" },
        ["SettingsOfficeAutoSaveIntervalHint"] = new[] { "Seconds between two automatic writes.", "两次自动写入之间的秒数。" },
        ["SettingsOfficeMacroSecurity"] = new[] { "Macro security", "宏安全级别" },
        ["SettingsOfficeMacroSecurityHint"] = new[] { "Prompt asks before running a macro; Disabled refuses silently; Enabled runs directly.", "「询问」在运行宏前确认；「禁用」直接拒绝；「启用」直接运行。" },
        ["SettingsMacroDisabled"] = new[] { "Disabled", "禁用" },
        ["SettingsMacroPrompt"] = new[] { "Prompt", "询问" },
        ["SettingsMacroEnabled"] = new[] { "Enabled", "启用" },
        ["SettingsReset"] = new[] { "Restore defaults", "恢复默认值" },
        ["CtxOpenWith"] = new[] { "Open with", "打开方式" },
        ["CtxOpenCode"] = new[] { "Code editor", "代码编辑器" },
        ["CtxOpenOffice"] = new[] { "Office document", "Office 文档" },
        ["CtxOpenPreview"] = new[] { "Quick look", "快速查看" },
        ["CtxOpenSystem"] = new[] { "System default app", "系统默认应用" },
        ["CtxOpenNewTab"] = new[] { "Open in new tab", "在新标签页中打开" },
        ["CtxCopyName"] = new[] { "Copy name", "复制名称" },
        ["CtxOpenTerminal"] = new[] { "Open in terminal", "在终端中打开" },
        ["CtxSortBy"] = new[] { "Sort by", "排序方式" },
        ["CtxSortAsc"] = new[] { "Ascending", "升序" },
        ["CtxSortDesc"] = new[] { "Descending", "降序" },
        ["CtxView"] = new[] { "View", "视图" },
        ["CtxViewDetails"] = new[] { "Details", "详细信息" },
        ["CtxViewIcons"] = new[] { "Icons", "图标" },
        ["MsgNameCopied"] = new[] { "Name copied", "名称已复制" },
        ["MsgTerminalUnavailable"] = new[] { "No terminal here", "此处无法打开终端" },
        ["MsgTerminalFailed"] = new[] { "Cannot open terminal: {0}", "无法打开终端：{0}" },
        ["SettingsTooltip"] = new[] { "Settings and extensions", "设置与扩展" },
        ["ClosePage"] = new[] { "Close page", "关闭页面" },
        ["TransfersTitle"] = new[] { "Transfers", "传输队列" },
        ["TransfersClear"] = new[] { "Clear finished", "清除已完成" },
        ["QuickLookNoPreview"] = new[] { "No preview available for this type", "此类型暂无可用预览" },
        ["PalettePlaceholder"] = new[] { "Type a command", "输入命令" },
        ["PaletteEmpty"] = new[] { "No matching command", "没有匹配的命令" },
        ["PaletteHint"] = new[] { "Enter runs the selected command. Esc closes.", "回车执行选中命令，Esc 关闭" },
        ["DualPane"] = new[] { "Dual pane", "双栏对照" },
        ["BatchRename"] = new[] { "Batch rename", "批量重命名" },
        ["Compress"] = new[] { "Compress", "压缩" },
        ["Extract"] = new[] { "Extract", "解压" },
        ["AddRemoteLocation"] = new[] { "Add network location", "添加网络位置" },
        ["StatusWebResolving"] = new[] { "Resolving URL...", "正在解析网址..." },
        ["StatusWebDownloading"] = new[] { "Downloading {0}...", "正在下载 {0}..." },
        ["StatusWebProgress"] = new[] { "Downloading... {0}%", "正在下载... {0}%" },
        ["MsgWebPageTitle"] = new[] { "Opened in the browser", "已在浏览器打开" },
        ["MsgWebPageBody"] = new[] { "{0} is a web page, not a folder, so it was opened in your default browser.", "{0} 是网页而不是文件夹，已交给默认浏览器打开。" },
        ["MsgWebDownloaded"] = new[] { "Downloaded", "已下载" },
        ["MsgWebFailed"] = new[] { "Cannot open this URL", "无法打开该网址" },
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
    public static string AddressUrlHint => Loc.T("AddressUrlHint");
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
    public static string WinMinimize => Loc.T("WinMinimize");
    public static string WinMaximize => Loc.T("WinMaximize");
    public static string WinClose => Loc.T("WinClose");
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
    public static string OfficeTab => Loc.T("OfficeTab");
    public static string OfficeBackToFiles => Loc.T("OfficeBackToFiles");
    public static string OfficeDocumentModeHint => Loc.T("OfficeDocumentModeHint");
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
    public static string OfficeSaveAs => Loc.T("OfficeSaveAs");
    public static string OfficeExportHtml => Loc.T("OfficeExportHtml");
    public static string OfficeUndo => Loc.T("OfficeUndo");
    public static string OfficeRedo => Loc.T("OfficeRedo");
    public static string OfficeFind => Loc.T("OfficeFind");
    public static string OfficeReplace => Loc.T("OfficeReplace");
    public static string OfficeFindCount => Loc.F("OfficeFindCount", "{0}");
    public static string OfficeReplaceCount => Loc.F("OfficeReplaceCount", "{0}");
    public static string OfficeStyle => Loc.T("OfficeStyle");
    public static string OfficeFormulaHint => Loc.T("OfficeFormulaHint");
    public static string OfficeLayout => Loc.T("OfficeLayout");
    public static string OfficeNotes => Loc.T("OfficeNotes");
    public static string OfficeNotesHint => Loc.T("OfficeNotesHint");
    public static string OfficeGroupParagraph => Loc.T("OfficeGroupParagraph");
    public static string OfficeAlignLeft => Loc.T("OfficeAlignLeft");
    public static string OfficeAlignCenter => Loc.T("OfficeAlignCenter");
    public static string OfficeAlignRight => Loc.T("OfficeAlignRight");
    public static string OfficeAlignJustify => Loc.T("OfficeAlignJustify");
    public static string OfficeBullets => Loc.T("OfficeBullets");
    public static string OfficeNumbering => Loc.T("OfficeNumbering");
    public static string OfficeNoList => Loc.T("OfficeNoList");
    public static string OfficeAddParagraph => Loc.T("OfficeAddParagraph");
    public static string OfficeDeleteParagraph => Loc.T("OfficeDeleteParagraph");
    public static string OfficeAddTable => Loc.T("OfficeAddTable");
    public static string OfficeInsertImage => Loc.T("OfficeInsertImage");
    public static string OfficeInsertLink => Loc.T("OfficeInsertLink");
    public static string OfficeInsertLinkHint => Loc.T("OfficeInsertLinkHint");
    public static string OfficeInsert => Loc.T("OfficeInsert");
    public static string OfficeTabLayout => Loc.T("OfficeTabLayout");
    public static string OfficeTabReview => Loc.T("OfficeTabReview");
    public static string OfficeTabFormulas => Loc.T("OfficeTabFormulas");
    public static string OfficeTabData => Loc.T("OfficeTabData");
    public static string OfficeTabDesign => Loc.T("OfficeTabDesign");
    public static string OfficeInsertRow => Loc.T("OfficeInsertRow");
    public static string OfficeDeleteRow => Loc.T("OfficeDeleteRow");
    public static string OfficeInsertColumn => Loc.T("OfficeInsertColumn");
    public static string OfficeDeleteColumn => Loc.T("OfficeDeleteColumn");
    public static string OfficeRecalculate => Loc.T("OfficeRecalculate");
    public static string OfficeAddSheet => Loc.T("OfficeAddSheet");
    public static string OfficeRenameSheet => Loc.T("OfficeRenameSheet");
    public static string OfficeDeleteSheet => Loc.T("OfficeDeleteSheet");
    public static string OfficeDeleteSheetHint => Loc.F("OfficeDeleteSheetHint", "{0}");
    public static string OfficeSheetNameHint => Loc.T("OfficeSheetNameHint");
    public static string OfficeAddSlide => Loc.T("OfficeAddSlide");
    public static string OfficeDuplicateSlide => Loc.T("OfficeDuplicateSlide");
    public static string OfficeDeleteSlide => Loc.T("OfficeDeleteSlide");
    public static string OfficeAddTextBox => Loc.T("OfficeAddTextBox");
    public static string OfficeLayoutTitle => Loc.T("OfficeLayoutTitle");
    public static string OfficeLayoutBlank => Loc.T("OfficeLayoutBlank");
    public static string OfficeSum => Loc.T("OfficeSum");
    public static string OfficeAverage => Loc.T("OfficeAverage");
    public static string OfficeCount => Loc.T("OfficeCount");
    public static string OfficeSavedFile => Loc.F("OfficeSavedFile", "{0}");
    public static string OfficeExported => Loc.F("OfficeExported", "{0}");
    public static string OfficeClickTitle => Loc.T("OfficeClickTitle");
    public static string OfficeClickText => Loc.T("OfficeClickText");
    public static string OfficeAdvancedPanel => Loc.T("OfficeAdvancedPanel");
    public static string OfficeMultilevel => Loc.T("OfficeMultilevel");
    public static string OfficeRevisions => Loc.T("OfficeRevisions");
    public static string OfficeTrackChanges => Loc.T("OfficeTrackChanges");
    public static string OfficeAccept => Loc.T("OfficeAccept");
    public static string OfficeReject => Loc.T("OfficeReject");
    public static string OfficeAcceptAll => Loc.T("OfficeAcceptAll");
    public static string OfficeRejectAll => Loc.T("OfficeRejectAll");
    public static string OfficeCharts => Loc.T("OfficeCharts");
    public static string OfficeChartColumn => Loc.T("OfficeChartColumn");
    public static string OfficeChartBar => Loc.T("OfficeChartBar");
    public static string OfficeChartLine => Loc.T("OfficeChartLine");
    public static string OfficeChartPie => Loc.T("OfficeChartPie");
    public static string OfficeChartArea => Loc.T("OfficeChartArea");
    public static string OfficePivotTables => Loc.T("OfficePivotTables");
    public static string OfficePivotCreate => Loc.T("OfficePivotCreate");
    public static string OfficePivotRefresh => Loc.T("OfficePivotRefresh");
    public static string OfficeChartNoData => Loc.T("OfficeChartNoData");
    public static string OfficeChartCreated => Loc.F("OfficeChartCreated", "{0}");
    public static string OfficePivotCreated => Loc.F("OfficePivotCreated", "{0}");
    public static string OfficeTrackingOn => Loc.T("OfficeTrackingOn");
    public static string OfficeTrackingOff => Loc.T("OfficeTrackingOff");
    public static string OfficeObjects => Loc.T("OfficeObjects");
    public static string OfficeMacroRecord => Loc.T("OfficeMacroRecord");
    public static string OfficeMacroStop => Loc.T("OfficeMacroStop");
    public static string OfficeMacroSaved => Loc.F("OfficeMacroSaved", "{0}");
    public static string OfficeMacroRan => Loc.F("OfficeMacroRan", "{0}");
    public static string OfficeMacroRecording => Loc.T("OfficeMacroRecording");
    public static string OfficeVba => Loc.T("OfficeVba");
    public static string OfficeMacroSecurity => Loc.T("OfficeMacroSecurity");
    public static string OfficeVbaModules => Loc.T("OfficeVbaModules");
    public static string OfficeRevisionsTab => Loc.T("OfficeRevisionsTab");
    public static string OfficeNumberingTab => Loc.T("OfficeNumberingTab");
    public static string OfficeChartsTab => Loc.T("OfficeChartsTab");
    public static string OfficePivotsTab => Loc.T("OfficePivotsTab");
    public static string OfficeMacrosTab => Loc.T("OfficeMacrosTab");
    public static string OfficeObjectsTab => Loc.T("OfficeObjectsTab");
    public static string OfficeExtract => Loc.T("OfficeExtract");
    public static string OfficeNoRevisions => Loc.T("OfficeNoRevisions");
    public static string OfficeNoCharts => Loc.T("OfficeNoCharts");
    public static string OfficeNoPivots => Loc.T("OfficeNoPivots");
    public static string OfficeNoObjects => Loc.T("OfficeNoObjects");
    public static string OfficeNoModules => Loc.T("OfficeNoModules");
    public static string OfficeNoMacros => Loc.T("OfficeNoMacros");
    public static string OfficeSource => Loc.T("OfficeSource");
    public static string OfficeAuthor => Loc.T("OfficeAuthor");
    public static string OfficeValue => Loc.T("OfficeValue");
    public static string OfficeLevel => Loc.T("OfficeLevel");
    public static string OfficeRunMacro => Loc.T("OfficeRunMacro");
    public static string OfficeDeleteMacro => Loc.T("OfficeDeleteMacro");
    public static string OfficeWpsDecoded => Loc.F("OfficeWpsDecoded", "{0}");
    public static string OfficeRichDisclaimer => Loc.T("OfficeRichDisclaimer");
    public static string OfficeDisclaimer => Loc.T("OfficeDisclaimer");

    public static string ExtensionsTab => Loc.T("ExtensionsTab");
    public static string SettingsSubtitle => Loc.T("SettingsSubtitle");
    public static string SettingsNavGeneral => Loc.T("SettingsNavGeneral");
    public static string SettingsNavAppearance => Loc.T("SettingsNavAppearance");
    public static string SettingsNavAbout => Loc.T("SettingsNavAbout");
    public static string SettingsViewSection => Loc.T("SettingsViewSection");
    public static string SettingsShowHidden => Loc.T("SettingsShowHidden");
    public static string SettingsShowHiddenHint => Loc.T("SettingsShowHiddenHint");
    public static string SettingsSidebar => Loc.T("SettingsSidebar");
    public static string SettingsSidebarHint => Loc.T("SettingsSidebarHint");
    public static string SettingsDetailsPane => Loc.T("SettingsDetailsPane");
    public static string SettingsDetailsPaneHint => Loc.T("SettingsDetailsPaneHint");
    public static string SettingsDualPaneHint => Loc.T("SettingsDualPaneHint");
    public static string SettingsLanguageSection => Loc.T("SettingsLanguageSection");
    public static string SettingsLanguage => Loc.T("SettingsLanguage");
    public static string SettingsAppearanceTheme => Loc.T("SettingsAppearanceTheme");
    public static string SettingsThemeHint => Loc.T("SettingsThemeHint");
    public static string SettingsExtensionsHint => Loc.T("SettingsExtensionsHint");
    public static string SettingsAboutVersion => Loc.T("SettingsAboutVersion");
    public static string SettingsAboutBuild => Loc.T("SettingsAboutBuild");
    public static string SettingsAboutLicense => Loc.T("SettingsAboutLicense");
    public static string SettingsAboutBody => Loc.T("SettingsAboutBody");
    public static string SettingsOpenProject => Loc.T("SettingsOpenProject");
    public static string SettingsOpenNotices => Loc.T("SettingsOpenNotices");
    public static string SettingsOpenLicense => Loc.T("SettingsOpenLicense");
    public static string SettingsNavFiles => Loc.T("SettingsNavFiles");
    public static string SettingsNavCode => Loc.T("SettingsNavCode");
    public static string SettingsNavOffice => Loc.T("SettingsNavOffice");
    public static string SettingsGeneralHint => Loc.T("SettingsGeneralHint");
    public static string SettingsWindowSection => Loc.T("SettingsWindowSection");
    public static string SettingsThemeSection => Loc.T("SettingsThemeSection");
    public static string SettingsFilesHint => Loc.T("SettingsFilesHint");
    public static string SettingsFilesListSection => Loc.T("SettingsFilesListSection");
    public static string SettingsShowExtensions => Loc.T("SettingsShowExtensions");
    public static string SettingsShowExtensionsHint => Loc.T("SettingsShowExtensionsHint");
    public static string SettingsFoldersFirst => Loc.T("SettingsFoldersFirst");
    public static string SettingsFoldersFirstHint => Loc.T("SettingsFoldersFirstHint");
    public static string SettingsThumbnails => Loc.T("SettingsThumbnails");
    public static string SettingsThumbnailsHint => Loc.T("SettingsThumbnailsHint");
    public static string SettingsFilesSortSection => Loc.T("SettingsFilesSortSection");
    public static string SettingsDefaultView => Loc.T("SettingsDefaultView");
    public static string SettingsDefaultViewHint => Loc.T("SettingsDefaultViewHint");
    public static string SettingsSortDefault => Loc.T("SettingsSortDefault");
    public static string SettingsSortDefaultHint => Loc.T("SettingsSortDefaultHint");
    public static string SettingsSortDescending => Loc.T("SettingsSortDescending");
    public static string SettingsFilesDeleteSection => Loc.T("SettingsFilesDeleteSection");
    public static string SettingsConfirmDelete => Loc.T("SettingsConfirmDelete");
    public static string SettingsConfirmDeleteHint => Loc.T("SettingsConfirmDeleteHint");
    public static string SettingsCodeHint => Loc.T("SettingsCodeHint");
    public static string SettingsCodeFontSection => Loc.T("SettingsCodeFontSection");
    public static string SettingsCodeFontFamily => Loc.T("SettingsCodeFontFamily");
    public static string SettingsCodeFontFamilyHint => Loc.T("SettingsCodeFontFamilyHint");
    public static string SettingsCodeFontSize => Loc.T("SettingsCodeFontSize");
    public static string SettingsCodeFontSizeHint => Loc.T("SettingsCodeFontSizeHint");
    public static string SettingsCodeIndentSection => Loc.T("SettingsCodeIndentSection");
    public static string SettingsCodeTabSize => Loc.T("SettingsCodeTabSize");
    public static string SettingsCodeTabSizeHint => Loc.T("SettingsCodeTabSizeHint");
    public static string SettingsCodeInsertSpaces => Loc.T("SettingsCodeInsertSpaces");
    public static string SettingsCodeInsertSpacesHint => Loc.T("SettingsCodeInsertSpacesHint");
    public static string SettingsCodeViewSection => Loc.T("SettingsCodeViewSection");
    public static string SettingsCodeWordWrap => Loc.T("SettingsCodeWordWrap");
    public static string SettingsCodeWordWrapHint => Loc.T("SettingsCodeWordWrapHint");
    public static string SettingsCodeLineNumbers => Loc.T("SettingsCodeLineNumbers");
    public static string SettingsCodeLineNumbersHint => Loc.T("SettingsCodeLineNumbersHint");
    public static string SettingsCodeWhitespace => Loc.T("SettingsCodeWhitespace");
    public static string SettingsCodeWhitespaceHint => Loc.T("SettingsCodeWhitespaceHint");
    public static string SettingsCodeMinimap => Loc.T("SettingsCodeMinimap");
    public static string SettingsCodeMinimapHint => Loc.T("SettingsCodeMinimapHint");
    public static string SettingsCodeBrackets => Loc.T("SettingsCodeBrackets");
    public static string SettingsCodeBracketsHint => Loc.T("SettingsCodeBracketsHint");
    public static string SettingsCodeCursor => Loc.T("SettingsCodeCursor");
    public static string SettingsCodeCursorHint => Loc.T("SettingsCodeCursorHint");
    public static string SettingsCodeSmooth => Loc.T("SettingsCodeSmooth");
    public static string SettingsCodeSmoothHint => Loc.T("SettingsCodeSmoothHint");
    public static string SettingsCodeSaveSection => Loc.T("SettingsCodeSaveSection");
    public static string SettingsCodeAutoSave => Loc.T("SettingsCodeAutoSave");
    public static string SettingsCodeAutoSaveHint => Loc.T("SettingsCodeAutoSaveHint");
    public static string SettingsCodeAutoSaveDelay => Loc.T("SettingsCodeAutoSaveDelay");
    public static string SettingsCodeAutoSaveDelayHint => Loc.T("SettingsCodeAutoSaveDelayHint");
    public static string SettingsCodeTheme => Loc.T("SettingsCodeTheme");
    public static string SettingsCodeThemeHint => Loc.T("SettingsCodeThemeHint");
    public static string SettingsWrapOff => Loc.T("SettingsWrapOff");
    public static string SettingsWrapOn => Loc.T("SettingsWrapOn");
    public static string SettingsWrapColumn => Loc.T("SettingsWrapColumn");
    public static string SettingsLineNumbersOn => Loc.T("SettingsLineNumbersOn");
    public static string SettingsLineNumbersRelative => Loc.T("SettingsLineNumbersRelative");
    public static string SettingsLineNumbersOff => Loc.T("SettingsLineNumbersOff");
    public static string SettingsWhitespaceNone => Loc.T("SettingsWhitespaceNone");
    public static string SettingsWhitespaceBoundary => Loc.T("SettingsWhitespaceBoundary");
    public static string SettingsWhitespaceSelection => Loc.T("SettingsWhitespaceSelection");
    public static string SettingsWhitespaceAll => Loc.T("SettingsWhitespaceAll");
    public static string SettingsCursorLine => Loc.T("SettingsCursorLine");
    public static string SettingsCursorBlock => Loc.T("SettingsCursorBlock");
    public static string SettingsCursorUnderline => Loc.T("SettingsCursorUnderline");
    public static string SettingsAutoSaveOff => Loc.T("SettingsAutoSaveOff");
    public static string SettingsAutoSaveDelay => Loc.T("SettingsAutoSaveDelay");
    public static string SettingsOfficeHint => Loc.T("SettingsOfficeHint");
    public static string SettingsOfficeDocSection => Loc.T("SettingsOfficeDocSection");
    public static string SettingsOfficeDefaultKind => Loc.T("SettingsOfficeDefaultKind");
    public static string SettingsOfficeDefaultKindHint => Loc.T("SettingsOfficeDefaultKindHint");
    public static string SettingsOfficeFontSize => Loc.T("SettingsOfficeFontSize");
    public static string SettingsOfficeFontSizeHint => Loc.T("SettingsOfficeFontSizeHint");
    public static string SettingsOfficeZoom => Loc.T("SettingsOfficeZoom");
    public static string SettingsOfficeZoomHint => Loc.T("SettingsOfficeZoomHint");
    public static string SettingsOfficeViewSection => Loc.T("SettingsOfficeViewSection");
    public static string SettingsOfficeRuler => Loc.T("SettingsOfficeRuler");
    public static string SettingsOfficeRulerHint => Loc.T("SettingsOfficeRulerHint");
    public static string SettingsOfficeWidePage => Loc.T("SettingsOfficeWidePage");
    public static string SettingsOfficeWidePageHint => Loc.T("SettingsOfficeWidePageHint");
    public static string SettingsOfficeGridLines => Loc.T("SettingsOfficeGridLines");
    public static string SettingsOfficeGridLinesHint => Loc.T("SettingsOfficeGridLinesHint");
    public static string SettingsOfficeTrackChanges => Loc.T("SettingsOfficeTrackChanges");
    public static string SettingsOfficeTrackChangesHint => Loc.T("SettingsOfficeTrackChangesHint");
    public static string SettingsOfficeSaveSection => Loc.T("SettingsOfficeSaveSection");
    public static string SettingsOfficeAutoSave => Loc.T("SettingsOfficeAutoSave");
    public static string SettingsOfficeAutoSaveHint => Loc.T("SettingsOfficeAutoSaveHint");
    public static string SettingsOfficeAutoSaveInterval => Loc.T("SettingsOfficeAutoSaveInterval");
    public static string SettingsOfficeAutoSaveIntervalHint => Loc.T("SettingsOfficeAutoSaveIntervalHint");
    public static string SettingsOfficeMacroSecurity => Loc.T("SettingsOfficeMacroSecurity");
    public static string SettingsOfficeMacroSecurityHint => Loc.T("SettingsOfficeMacroSecurityHint");
    public static string SettingsMacroDisabled => Loc.T("SettingsMacroDisabled");
    public static string SettingsMacroPrompt => Loc.T("SettingsMacroPrompt");
    public static string SettingsMacroEnabled => Loc.T("SettingsMacroEnabled");
    public static string SettingsReset => Loc.T("SettingsReset");
    public static string CtxOpenWith => Loc.T("CtxOpenWith");
    public static string CtxOpenCode => Loc.T("CtxOpenCode");
    public static string CtxOpenOffice => Loc.T("CtxOpenOffice");
    public static string CtxOpenPreview => Loc.T("CtxOpenPreview");
    public static string CtxOpenSystem => Loc.T("CtxOpenSystem");
    public static string CtxOpenNewTab => Loc.T("CtxOpenNewTab");
    public static string CtxCopyName => Loc.T("CtxCopyName");
    public static string CtxOpenTerminal => Loc.T("CtxOpenTerminal");
    public static string CtxSortBy => Loc.T("CtxSortBy");
    public static string CtxSortAsc => Loc.T("CtxSortAsc");
    public static string CtxSortDesc => Loc.T("CtxSortDesc");
    public static string CtxView => Loc.T("CtxView");
    public static string CtxViewDetails => Loc.T("CtxViewDetails");
    public static string CtxViewIcons => Loc.T("CtxViewIcons");
    public static string MsgNameCopied => Loc.T("MsgNameCopied");
    public static string MsgTerminalUnavailable => Loc.T("MsgTerminalUnavailable");
    public static string MsgTerminalFailed => Loc.T("MsgTerminalFailed");
    public static string SettingsTooltip => Loc.T("SettingsTooltip");
    public static string ClosePage => Loc.T("ClosePage");
    public static string TransfersTitle => Loc.T("TransfersTitle");
    public static string TransfersClear => Loc.T("TransfersClear");
    public static string QuickLookNoPreview => Loc.T("QuickLookNoPreview");
    public static string PalettePlaceholder => Loc.T("PalettePlaceholder");
    public static string PaletteEmpty => Loc.T("PaletteEmpty");
    public static string PaletteHint => Loc.T("PaletteHint");
    public static string DualPane => Loc.T("DualPane");
    public static string BatchRename => Loc.T("BatchRename");
    public static string Compress => Loc.T("Compress");
    public static string Extract => Loc.T("Extract");
    public static string AddRemoteLocation => Loc.T("AddRemoteLocation");
    public static string StatusWebResolving => Loc.T("StatusWebResolving");
    public static string StatusWebDownloading => Loc.T("StatusWebDownloading");
    public static string StatusWebProgress => Loc.T("StatusWebProgress");
    public static string MsgWebPageTitle => Loc.T("MsgWebPageTitle");
    public static string MsgWebPageBody => Loc.T("MsgWebPageBody");
    public static string MsgWebDownloaded => Loc.T("MsgWebDownloaded");
    public static string MsgWebFailed => Loc.T("MsgWebFailed");
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
