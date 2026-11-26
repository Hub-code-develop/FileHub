// WinUI 3 for Avalonia — TabView tab strip.
//
// Ported from WinUI 3 TabView:
//   microsoft/microsoft-ui-xaml  controls/dev/TabView/TabView.cpp
//                               controls/dev/TabView/TabViewItem.cpp
// Copyright (c) Microsoft Corporation. Licensed under the MIT License.
// See THIRD-PARTY-NOTICES.md.
//
// Only the tab strip is modelled here, not the tab content: WinUI's TabView hosts a
// content presenter per tab, while FileHub switches a single content area by view model.
// Everything visible on the strip — 40 px strip, 32 px items, 8 px top corners, the close
// button and the overflow scroll — is what this control reproduces. The interaction layer
// (middle-click close, drag reorder) follows WinUI's TabView behaviour.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace WinUI3.Fluent.Controls;

/// <summary>Arguments for a tab reorder request raised by <see cref="WinTabStrip"/>.</summary>
public sealed class TabReorderEventArgs : RoutedEventArgs
{
    public TabReorderEventArgs(int fromIndex, int toIndex) : base(WinTabStrip.TabReorderedEvent)
    {
        FromIndex = fromIndex;
        ToIndex = toIndex;
    }

    public int FromIndex { get; }
    public int ToIndex { get; }
}

/// <summary>
/// The tab strip of a WinUI 3 <c>TabView</c>: a horizontal, single-selection list whose
/// containers carry a header and a close button.
/// </summary>
public class WinTabStrip : ListBox
{
    /// <summary>Raised when a tab is dragged onto another slot.</summary>
    public static readonly RoutedEvent<TabReorderEventArgs> TabReorderedEvent =
        RoutedEvent.Register<TabReorderEventArgs>(nameof(TabReordered), RoutingStrategies.Bubble,
            typeof(WinTabStrip));

    static WinTabStrip()
    {
        SelectionModeProperty.OverrideDefaultValue<WinTabStrip>(SelectionMode.Single);
        // The strip stays put while tabs are added; only one tab is ever focused.
        FocusableProperty.OverrideDefaultValue<WinTabStrip>(false);
    }

    public WinTabStrip()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    public event EventHandler<TabReorderEventArgs>? TabReordered
    {
        add => AddHandler(TabReorderedEvent, value);
        remove => RemoveHandler(TabReorderedEvent, value);
    }

    protected override Type StyleKeyOverride => typeof(WinTabStrip);

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        recycleKey = null;
        return item is not WinTabStripItem;
    }

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey)
        => new WinTabStripItem();

    /// <summary>Index of the item under a point in strip coordinates, or -1.</summary>
    internal int IndexAt(Point position)
    {
        for (var i = 0; i < ItemCount; i++)
        {
            if (ContainerFromIndex(i) is not { } container) continue;
            var origin = container.TranslatePoint(new Point(0, 0), this);
            if (origin is not null && position.X >= origin.Value.X && position.X < origin.Value.X + container.Bounds.Width)
                return i;
        }
        return -1;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (!TabDragPayload.TryRead(e.DataTransfer, out _))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        e.DragEffects = DragDropEffects.Move;
        Highlight(IndexAt(e.GetPosition(this)));
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        Highlight(-1);
        if (!TabDragPayload.TryRead(e.DataTransfer, out var fromIndex)) return;
        var toIndex = IndexAt(e.GetPosition(this));
        e.Handled = true;
        if (toIndex >= 0 && toIndex != fromIndex)
            RaiseEvent(new TabReorderEventArgs(fromIndex, toIndex));
    }

    private void Highlight(int index)
    {
        for (var i = 0; i < ItemCount; i++)
        {
            if (ContainerFromIndex(i) is { } container)
                container.Classes.Set("drop-target", i == index);
        }
    }
}

/// <summary>
/// A single tab in a <see cref="WinTabStrip"/>. Mirrors WinUI 3's <c>TabViewItem</c>
/// header: 32 px tall, 8 px top corners, icon + title, and a close button that becomes
/// visible while the pointer is over the tab or while the tab is selected.
/// </summary>
public class WinTabStripItem : ListBoxItem
{
    /// <summary>Shows or hides the close button (WinUI: <c>IsClosable</c>).</summary>
    public static readonly StyledProperty<bool> IsClosableProperty =
        AvaloniaProperty.Register<WinTabStripItem, bool>(nameof(IsClosable), true);

    /// <summary>Raised when the user activates the tab's close button.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> CloseRequestedEvent =
        RoutedEvent.Register<WinTabStripItem, RoutedEventArgs>(
            nameof(CloseRequested), RoutingStrategies.Bubble);

    /// <summary>Raised when the user requests the tab's context menu.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> ContextMenuRequestedEvent =
        RoutedEvent.Register<WinTabStripItem, RoutedEventArgs>(
            nameof(ContextMenuRequested), RoutingStrategies.Bubble);

    private Button? _closeButton;

    public bool IsClosable
    {
        get => GetValue(IsClosableProperty);
        set => SetValue(IsClosableProperty, value);
    }

    public event EventHandler<RoutedEventArgs>? CloseRequested
    {
        add => AddHandler(CloseRequestedEvent, value);
        remove => RemoveHandler(CloseRequestedEvent, value);
    }

    public event EventHandler<RoutedEventArgs>? ContextMenuRequested
    {
        add => AddHandler(ContextMenuRequestedEvent, value);
        remove => RemoveHandler(ContextMenuRequestedEvent, value);
    }

    protected override Type StyleKeyOverride => typeof(WinTabStripItem);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_closeButton is not null) _closeButton.Click -= OnCloseButtonClick;

        base.OnApplyTemplate(e);

        _closeButton = e.NameScope.Find<Button>("PART_CloseButton");
        if (_closeButton is not null) _closeButton.Click += OnCloseButtonClick;
    }

    protected override async void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var point = e.GetCurrentPoint(this).Properties;
        if (point.IsMiddleButtonPressed)
        {
            e.Handled = true;
            RaiseEvent(new RoutedEventArgs(CloseRequestedEvent, this));
            return;
        }

        if (point.IsRightButtonPressed)
        {
            e.Handled = true;
            RaiseEvent(new RoutedEventArgs(ContextMenuRequestedEvent, this));
            return;
        }

        if (!point.IsLeftButtonPressed) return;

        var strip = this.FindAncestorOfType<WinTabStrip>();
        if (strip is null) return;
        var index = strip.IndexFromContainer(this);
        if (index < 0) return;

        // DoDragDropAsync owns the move threshold, so a plain click still selects the tab.
        await DragDrop.DoDragDropAsync(e, TabDragPayload.Create(index), DragDropEffects.Move);
    }

    private void OnCloseButtonClick(object? sender, RoutedEventArgs e)
    {
        // Keep the click from reaching the tab itself (which would select or drag it).
        e.Handled = true;
        RaiseEvent(new RoutedEventArgs(CloseRequestedEvent, this));
    }
}

/// <summary>Private drag payload that keeps tab reordering inside the strip.</summary>
internal static class TabDragPayload
{
    private static readonly DataFormat<string> Format =
        DataFormat.CreateStringApplicationFormat("winui3.tab-index.v1");

    public static IDataTransfer Create(int index)
    {
        var item = new DataTransferItem();
        item.Set(Format, index.ToString());
        var transfer = new DataTransfer();
        transfer.Add(item);
        return transfer;
    }

    public static bool TryRead(IDataTransfer? data, out int index)
    {
        index = -1;
        if (data?.TryGetValue(Format) is not { } value) return false;
        return int.TryParse(value, out index);
    }
}
