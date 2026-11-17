using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Trader.Module.Controls;

public sealed record ChartSeries(string Name, string Color, IReadOnlyList<double> Values);

/// <summary>A minimal multi-series line chart with a shared y-axis and a legend.</summary>
public sealed class LineChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series),
        typeof(IReadOnlyList<ChartSeries>),
        typeof(LineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(LineChart),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueFormatProperty = DependencyProperty.Register(
        nameof(ValueFormat),
        typeof(string),
        typeof(LineChart),
        new FrameworkPropertyMetadata("N0", FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Typeface Font = new("Segoe UI");
    private static readonly Brush AxisBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8E, 0xA1, 0xB6)));
    private static readonly Pen GridPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x24, 0x31, 0x3F))), 1));

    public IReadOnlyList<ChartSeries>? Series
    {
        get => (IReadOnlyList<ChartSeries>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string ValueFormat
    {
        get => (string)GetValue(ValueFormatProperty);
        set => SetValue(ValueFormatProperty, value);
    }

    protected override void OnRender(DrawingContext context)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        DrawText(context, Title, new Point(4, 2), 12, AxisBrush, dpi);

        var series = Series?.Where(s => s.Values.Count > 1).ToArray() ?? [];
        if (series.Length == 0 || width < 80 || height < 60)
        {
            DrawText(context, "No data", new Point(width / 2 - 20, height / 2 - 8), 12, AxisBrush, dpi);
            return;
        }

        var min = series.Min(s => s.Values.Min());
        var max = series.Max(s => s.Values.Max());
        if (max - min < 1e-9)
        {
            max = min + 1;
        }

        var plot = new Rect(64, 22, Math.Max(10, width - 74), Math.Max(10, height - 48));
        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Top + plot.Height * i / 4;
            context.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var value = max - (max - min) * i / 4;
            DrawText(context, value.ToString(ValueFormat, CultureInfo.InvariantCulture), new Point(2, y - 8), 10, AxisBrush, dpi);
        }

        var legendX = plot.Left;
        foreach (var item in series)
        {
            var brush = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(item.Color)));
            var pen = Freeze(new Pen(brush, 1.5));
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                for (var i = 0; i < item.Values.Count; i++)
                {
                    var x = plot.Left + plot.Width * i / (item.Values.Count - 1);
                    var y = plot.Bottom - plot.Height * (item.Values[i] - min) / (max - min);
                    if (i == 0)
                    {
                        g.BeginFigure(new Point(x, y), false, false);
                    }
                    else
                    {
                        g.LineTo(new Point(x, y), true, false);
                    }
                }
            }

            geometry.Freeze();
            context.DrawGeometry(null, pen, geometry);
            context.DrawRectangle(brush, null, new Rect(legendX, plot.Bottom + 10, 10, 10));
            var label = new FormattedText(item.Name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 11, AxisBrush, dpi);
            context.DrawText(label, new Point(legendX + 14, plot.Bottom + 7));
            legendX += label.Width + 30;
        }
    }

    private static void DrawText(DrawingContext context, string text, Point origin, double size, Brush brush, double dpi)
    {
        if (!string.IsNullOrEmpty(text))
        {
            context.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, size, brush, dpi), origin);
        }
    }

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
