using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace TodoTracker.Tray;

/// <summary>The app's mark (one lit task above two faint ones, on the brand gradient), with a red dot when a reminder needs you.</summary>
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
                    new GradientStop(Color.FromRgb(0x4F, 0x46, 0xE5), 0),
                    new GradientStop(Color.FromRgb(0x7C, 0x3A, 0xED), 0.5),
                    new GradientStop(Color.FromRgb(0xDB, 0x27, 0x77), 1),
                },
            };
            ctx.DrawRectangle(brand, null, new Rect(0, 0, size, size), size * 0.22, size * 0.22);
            // One lit task (the thing to do now), then two faint ones: the same shapes as build/icons.py.
            var white = Brushes.White;
            ctx.DrawEllipse(white, null, new Point(size * 0.314, size * 0.322), size * 0.107, size * 0.107);
            ctx.DrawRectangle(white, null, new Rect(size * 0.471, size * 0.283, size * 0.324, size * 0.078), size * 0.039, size * 0.039);
            foreach (var (y, width, alpha) in new[] { (0.547, 0.293, 0.4), (0.723, 0.234, 0.25) })
            {
                var faint = new SolidColorBrush(Colors.White, alpha);
                ctx.DrawEllipse(faint, null, new Point(size * 0.314, size * y), size * 0.061, size * 0.061);
                ctx.DrawRectangle(faint, null, new Rect(size * 0.432, size * (y - 0.026), size * width, size * 0.051), size * 0.026, size * 0.026);
            }

            if (attention)
            {
                var d = size * 0.42;
                ctx.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), new Pen(Brushes.White, size * 0.05), new Rect(size - d, 0, d - 1, d - 1));
            }
        }

        return bitmap;
    }
}
