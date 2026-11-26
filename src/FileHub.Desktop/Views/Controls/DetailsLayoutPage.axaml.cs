using Avalonia.Controls;

namespace FileHub.Desktop.Views.Controls;

/// <summary>
/// Files App's DetailsLayoutPage, ported to Avalonia. Exposes the two list views so the
/// window can attach selection / drag-drop handlers and context menus (Files does the same
/// in its BaseLayoutPage code-behind).
/// </summary>
public partial class DetailsLayoutPage : UserControl
{
    public DetailsLayoutPage() => InitializeComponent();

    public ListBox FileList => _fileList;
    public ListBox GridList => _gridList;

    /// <summary>Lists that accept drops. Both switch visibility but keep their handlers.</summary>
    public IEnumerable<ListBox> FileViews => new[] { _fileList, _gridList };
}
