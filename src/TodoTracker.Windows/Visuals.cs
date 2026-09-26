using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace TodoTracker.Windows;

/// <summary>Lightweight circular progress (0..1) drawn directly; used for the focus timer.</summary>
public sealed class ProgressRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ProgressRing), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(ProgressRing), new FrameworkPropertyMetadata(Brushes.MediumSlateBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(ProgressRing), new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0x33, 0x94, 0xA3, 0xB8)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(ProgressRing), new FrameworkPropertyMetadata(3d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush Track
    {
        get => (Brush)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness)
        {
            return;
        }

        var radius = (size - Thickness) / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        drawingContext.DrawEllipse(null, new Pen(Track, Thickness), center, radius, radius);

        var value = Math.Clamp(Value, 0, 1);
        if (value <= 0)
        {
            return;
        }

        var pen = new Pen(Stroke, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (value >= 0.9999)
        {
            drawingContext.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        var angle = (value * 360) - 90;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + (radius * Math.Cos(angle * Math.PI / 180)), center.Y + (radius * Math.Sin(angle * Math.PI / 180)));
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, isFilled: false, isClosed: false);
            ctx.ArcTo(end, new Size(radius, radius), 0, value > 0.5, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(null, pen, geometry);
    }
}

/// <summary>Maps a chip tone (step, info, warn, danger, muted) to its text ("fg") or fill ("bg") brush.</summary>
public sealed class ToneBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, (SolidColorBrush Fg, SolidColorBrush Bg)> Tones = new(StringComparer.Ordinal)
    {
        ["step"] = (Brush(0xFF, 0x63, 0x66, 0xF1), Brush(0x26, 0x63, 0x66, 0xF1)),
        ["info"] = (Brush(0xFF, 0x0E, 0xA5, 0xE9), Brush(0x22, 0x0E, 0xA5, 0xE9)),
        ["warn"] = (Brush(0xFF, 0xD9, 0x77, 0x06), Brush(0x2A, 0xF5, 0x9E, 0x0B)),
        ["danger"] = (Brush(0xFF, 0xEF, 0x44, 0x44), Brush(0x22, 0xEF, 0x44, 0x44)),
        ["muted"] = (Brush(0xFF, 0x94, 0xA3, 0xB8), Brush(0x00, 0, 0, 0)),
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var tone = Tones.TryGetValue(value as string ?? "muted", out var t) ? t : Tones["muted"];
        return string.Equals(parameter as string, "bg", StringComparison.Ordinal) ? tone.Bg : tone.Fg;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();

    private static SolidColorBrush Brush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Hex color to a translucent brush (e.g. the soft halo around a priority dot or the priority pill fill).</summary>
public sealed class HexToSoftBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var color = (Color)ColorConverter.ConvertFromString(value as string ?? "#3b82f6");
        var alpha = parameter is string s && byte.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) ? a : (byte)0x30;
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
