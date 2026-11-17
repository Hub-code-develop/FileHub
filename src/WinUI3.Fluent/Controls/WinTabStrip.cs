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
// button, the overflow behaviour — is what this control reproduces.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace WinUI3.Fluent.Controls;

/// <summary>
/// The tab strip of a WinUI 3 <c>TabView</c>: a horizontal, single-selection list whose
/// containers carry a header and a close button.
/// </summary>
public class WinTabStrip : ListBox
{
    static WinTabStrip()
    {
        SelectionModeProperty.OverrideDefaultValue<WinTabStrip>(SelectionMode.Single);
        // The strip stays put while tabs are added; only one tab is ever focused.
        FocusableProperty.OverrideDefaultValue<WinTabStrip>(false);
    }

    protected override Type StyleKeyOverride => typeof(WinTabStrip);

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        recycleKey = null;
        return item is not WinTabStripItem;
    }

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey)
        => new WinTabStripItem();
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

    protected override Type StyleKeyOverride => typeof(WinTabStripItem);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_closeButton is not null) _closeButton.Click -= OnCloseButtonClick;

        base.OnApplyTemplate(e);

        _closeButton = e.NameScope.Find<Button>("PART_CloseButton");
        if (_closeButton is not null) _closeButton.Click += OnCloseButtonClick;
    }

    private void OnCloseButtonClick(object? sender, RoutedEventArgs e)
    {
        // Keep the click from reaching the tab itself (which would select or drag it).
        e.Handled = true;
        RaiseEvent(new RoutedEventArgs(CloseRequestedEvent, this));
    }
}
