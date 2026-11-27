using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using FileHub.Office.Models;

namespace FileHub.Desktop.Views.Controls;

/// <summary>
/// Lightweight native chart renderer. It builds standard Avalonia shapes so the preview has no
/// dependency on a charting package and looks consistent with the rest of the WinUI-style UI.
/// </summary>
public sealed class ChartPreviewControl : UserControl
{
    public static readonly StyledProperty<OfficeChart?> ChartProperty =
        AvaloniaProperty.Register<ChartPreviewControl, OfficeChart?>(nameof(Chart));

    private static readonly SolidColorBrush[] Palette =
    {
        new(Color.FromRgb(43, 87, 154)), new(Color.FromRgb(209, 52, 56)),
        new(Color.FromRgb(16, 124, 16)), new(Color.FromRgb(255, 192, 0)),
        new(Color.FromRgb(112, 48, 160))
    };

    private readonly Canvas _canvas = new();
    private readonly TextBlock _empty = new() { Text = "No data", Opacity = 0.5, Margin = new Thickness(8) };

    static ChartPreviewControl() => AffectsRender<ChartPreviewControl>(ChartProperty);

    public ChartPreviewControl()
    {
        Content = new Panel { Children = { _canvas, _empty } };
        ClipToBounds = true;
        SizeChanged += (_, _) => Rebuild();
    }

    public OfficeChart? Chart
    {
        get => GetValue(ChartProperty);
        set => SetValue(ChartProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ChartProperty) Rebuild();
    }

    private void Rebuild()
    {
        _canvas.Children.Clear();
        var chart = Chart;
        if (chart is null || chart.Series.Count == 0 || chart.Categories.Count == 0)
        {
            _empty.IsVisible = true;
            return;
        }
        _empty.IsVisible = false;
        var width = Math.Max(240, Bounds.Width);
        var height = Math.Max(90, Bounds.Height);
        var plotHeight = height - 26;
        var max = Math.Max(1, chart.MaxValue);
        var count = chart.Categories.Count;
        var band = (width - 36) / Math.Max(1, count);
        var barWidth = Math.Max(2, band * 0.72 / Math.Max(1, chart.Series.Count));

        var axis = new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
            Height = 1, Width = width - 24
        };
        Canvas.SetLeft(axis, 18);
        Canvas.SetTop(axis, plotHeight);
        _canvas.Children.Add(axis);

        for (var category = 0; category < count; category++)
        {
            for (var seriesIndex = 0; seriesIndex < chart.Series.Count; seriesIndex++)
            {
                var value = chart.Series[seriesIndex].Values.ElementAtOrDefault(category);
                var barHeight = Math.Max(1, Math.Abs(value) / max * (plotHeight - 8));
                var bar = new Rectangle
                {
                    Fill = Palette[seriesIndex % Palette.Length],
                    Width = barWidth,
                    Height = barHeight
                };
                Canvas.SetLeft(bar, 20 + category * band + seriesIndex * barWidth);
                Canvas.SetTop(bar, plotHeight - barHeight);
                _canvas.Children.Add(bar);
            }
            var label = new TextBlock
            {
                Text = chart.Categories[category],
                FontSize = 9,
                Width = band,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Canvas.SetLeft(label, 18 + category * band);
            Canvas.SetTop(label, plotHeight + 4);
            _canvas.Children.Add(label);
        }

        for (var i = 0; i < chart.Series.Count; i++)
        {
            var swatch = new Rectangle
            {
                Fill = Palette[i % Palette.Length],
                Width = 8, Height = 8
            };
            Canvas.SetLeft(swatch, 20 + i * 90);
            Canvas.SetTop(swatch, 2);
            _canvas.Children.Add(swatch);

            var name = new TextBlock { Text = chart.Series[i].Name, FontSize = 9 };
            Canvas.SetLeft(name, 32 + i * 90);
            Canvas.SetTop(name, 0);
            _canvas.Children.Add(name);
        }
    }
}
