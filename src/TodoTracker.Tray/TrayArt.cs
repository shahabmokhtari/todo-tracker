using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace TodoTracker.Tray;

/// <summary>The app's mark (a check on the brand gradient), with a red dot when a reminder needs you.</summary>
internal static class TrayArt
{
    public static Bitmap Draw(bool attention, int size = 64)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            var brand = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(0x63, 0x66, 0xF1), 0),
                    new GradientStop(Color.FromRgb(0x8B, 0x5C, 0xF6), 0.55),
                    new GradientStop(Color.FromRgb(0xEC, 0x48, 0x99), 1),
                },
            };
            ctx.DrawRectangle(brand, null, new Rect(0, 0, size, size), size * 0.22, size * 0.22);
            var pen = new Pen(Brushes.White, size * 0.11, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            var check = new StreamGeometry();
            using (var g = check.Open())
            {
                g.BeginFigure(new Point(size * 0.27, size * 0.52), false);
                g.LineTo(new Point(size * 0.43, size * 0.68));
                g.LineTo(new Point(size * 0.73, size * 0.34));
                g.EndFigure(false);
            }

            ctx.DrawGeometry(null, pen, check);
            if (attention)
            {
                var d = size * 0.42;
                ctx.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), new Pen(Brushes.White, size * 0.05), new Rect(size - d, 0, d - 1, d - 1));
            }
        }

        return bitmap;
    }
}
