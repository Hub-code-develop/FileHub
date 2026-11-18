// WinUI 3 for Avalonia — AppBarButton.
//
// Ported from WinUI 3:
//   microsoft/microsoft-ui-xaml  controls/dev/CommonStyles/AppBarButton_themeresources.xaml
// Copyright (c) Microsoft Corporation. Licensed under the MIT License.
// See THIRD-PARTY-NOTICES.md.
//
// Files App builds its whole toolbar out of AppBarButton, so this control is the piece
// that makes a Files-style CommandBar possible: 48 px row, 16 px icon, 12 px label,
// 2,6 inner-border inset that produces the 40 px hover pill.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;

namespace WinUI3.Fluent.Controls;

/// <summary>Where an <see cref="WinAppBarButton"/> puts its label.</summary>
public enum WinCommandBarLabelPosition
{
    /// <summary>Icon and label side by side (WinUI: <c>DefaultLabelPosition="Right"</c>).</summary>
    Right,

    /// <summary>Icon only; the label is still used for tooltips and automation.</summary>
    Collapsed,

    /// <summary>Icon above the label (WinUI: <c>Bottom</c>, 64 px tall).</summary>
    Bottom
}

/// <summary>
/// A WinUI 3 <c>AppBarButton</c>: the toolbar button Files App uses for every command.
/// </summary>
[PseudoClasses(":label-right", ":label-collapsed", ":label-bottom")]
public class WinAppBarButton : Button
{
    public static readonly StyledProperty<object?> IconProperty =
        AvaloniaProperty.Register<WinAppBarButton, object?>(nameof(Icon));

    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<WinAppBarButton, string>(nameof(Label), string.Empty);

    public static readonly StyledProperty<WinCommandBarLabelPosition> LabelPositionProperty =
        AvaloniaProperty.Register<WinAppBarButton, WinCommandBarLabelPosition>(
            nameof(LabelPosition), WinCommandBarLabelPosition.Right);

    public WinAppBarButton() => UpdateLabelPseudoClasses();

    /// <summary>The glyph, usually an icon control. Rendered at 16x16 like WinUI's Viewbox.</summary>
    public object? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public WinCommandBarLabelPosition LabelPosition
    {
        get => GetValue(LabelPositionProperty);
        set => SetValue(LabelPositionProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelPositionProperty) UpdateLabelPseudoClasses();
    }

    private void UpdateLabelPseudoClasses()
    {
        PseudoClasses.Set(":label-right", LabelPosition == WinCommandBarLabelPosition.Right);
        PseudoClasses.Set(":label-collapsed", LabelPosition == WinCommandBarLabelPosition.Collapsed);
        PseudoClasses.Set(":label-bottom", LabelPosition == WinCommandBarLabelPosition.Bottom);
    }
}
