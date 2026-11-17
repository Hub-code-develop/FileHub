// Ported from the Files App UI (https://github.com/files-community/Files),
// Copyright (c) 2018 - present Files Community, MIT License.
// See THIRD-PARTY-NOTICES.md. WinUI controls that have no equivalent in
// CodeHub-FluentUI are implemented here as native Avalonia controls.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;

namespace FileHub.Desktop.Controls;

/// <summary>
/// Files App NavigationToolbar button: 36x32, no border, 14px Fluent symbol.
/// Equivalent to the Back / Forward / Up / Refresh / Sidebar buttons in
/// src/Files.App/UserControls/NavigationToolbar.xaml.
/// </summary>
public sealed class ToolbarIconButton : Button
{
    public static readonly StyledProperty<FASymbol> SymbolProperty =
        AvaloniaProperty.Register<ToolbarIconButton, FASymbol>(nameof(Symbol), FASymbol.Folder);

    private readonly FASymbolIcon _icon;

    public ToolbarIconButton()
    {
        Width = 36;
        Height = 32;
        Padding = new Thickness(0);
        CornerRadius = new CornerRadius(4);
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        HorizontalContentAlignment = HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        _icon = new FASymbolIcon { Symbol = Symbol, FontSize = 14 };
        Content = _icon;
    }

    public FASymbol Symbol
    {
        get => GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SymbolProperty) _icon.Symbol = Symbol;
    }
}

/// <summary>
/// Files App AppBarButton with DefaultLabelPosition="Right": a 16px symbol followed by
/// a text label, inside the rounded command bar card.
/// Equivalent to src/Files.App/UserControls/Toolbar.xaml.
/// </summary>
public sealed class CommandBarButton : Button
{
    public static readonly StyledProperty<FASymbol> SymbolProperty =
        AvaloniaProperty.Register<CommandBarButton, FASymbol>(nameof(Symbol), FASymbol.Add);

    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<CommandBarButton, string>(nameof(Label), string.Empty);

    private readonly FASymbolIcon _icon;
    private readonly TextBlock _label;

    public CommandBarButton()
    {
        MinWidth = 40;
        Height = 34;
        Padding = new Thickness(10, 0, 12, 0);
        CornerRadius = new CornerRadius(4);
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Center;

        _icon = new FASymbolIcon { Symbol = Symbol, FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        _label = new TextBlock
        {
            Text = Label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _icon, _label }
        };
    }

    public FASymbol Symbol
    {
        get => GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SymbolProperty) _icon.Symbol = Symbol;
        if (change.Property == LabelProperty) _label.Text = Label;
    }
}

/// <summary>
/// Files App SidebarItem: 32px row, 16px symbol, rounded corners and a 3x16
/// selection pill. Equivalent to Files.App.Controls.SidebarItem in
/// src/Files.App/Views/MainPage.xaml (SidebarView.Footer / SidebarContent).
/// </summary>
public sealed class SidebarItem : Button
{
    public static readonly StyledProperty<FASymbol> SymbolProperty =
        AvaloniaProperty.Register<SidebarItem, FASymbol>(nameof(Symbol), FASymbol.Folder);

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<SidebarItem, string>(nameof(Text), string.Empty);

    public static readonly StyledProperty<string> DetailProperty =
        AvaloniaProperty.Register<SidebarItem, string>(nameof(Detail), string.Empty);

    public static readonly StyledProperty<bool> IsSelectedProperty =
        AvaloniaProperty.Register<SidebarItem, bool>(nameof(IsSelected));

    private readonly FASymbolIcon _icon;
    private readonly TextBlock _text;
    private readonly TextBlock _detail;
    private readonly Border _pill;

    public SidebarItem()
    {
        Height = 32;
        Padding = new Thickness(8, 0, 8, 0);
        CornerRadius = new CornerRadius(4);
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Center;

        _pill = new Border
        {
            Width = 3,
            Height = 16,
            CornerRadius = new CornerRadius(1.5),
            Background = new SolidColorBrush(Color.Parse("#FF0078D4")),
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center
        };
        _icon = new FASymbolIcon
        {
            Symbol = Symbol,
            FontSize = 16,
            Margin = new Thickness(8, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        _text = new TextBlock
        {
            Text = Text,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _detail = new TextBlock
        {
            Text = Detail,
            FontSize = 10,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsVisible = false
        };

        // Drive rows carry a second line ("94.21 GB / 926.3 GB"), so they are taller and the
        // capacity sits under the name instead of competing with it for horizontal space.
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 0
        };
        stack.Children.Add(_text);
        stack.Children.Add(_detail);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        Grid.SetColumn(_pill, 0);
        Grid.SetColumn(_icon, 1);
        Grid.SetColumn(stack, 2);
        grid.Children.Add(_pill);
        grid.Children.Add(_icon);
        grid.Children.Add(stack);
        Content = grid;
    }

    public FASymbol Symbol
    {
        get => GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Detail
    {
        get => GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    public bool IsSelected
    {
        get => GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SymbolProperty) _icon.Symbol = Symbol;
        if (change.Property == TextProperty) _text.Text = Text;
        if (change.Property == DetailProperty)
        {
            _detail.Text = Detail;
            var hasDetail = !string.IsNullOrEmpty(Detail);
            _detail.IsVisible = hasDetail;
            Height = hasDetail ? 40 : 32;
        }
        if (change.Property == IsSelectedProperty)
        {
            _pill.IsVisible = IsSelected;
            _text.FontWeight = IsSelected ? FontWeight.SemiBold : FontWeight.Normal;
        }
    }
}
