using System.Windows.Threading;
using TodoTracker.Desktop;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace TodoTracker.Windows;

/// <summary>
/// Onething in the notification area: click to bring the sidebar back, right-click for the menu (open, add a
/// task, the focus timer, hide or show the sidebar, quit). The tooltip says what's going on; a dot means a reminder.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly SidebarViewModel _vm;
    private readonly MainWindow _window;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(2) };
    private Drawing.Icon? _drawn;
    private bool? _drawnAttention;

    public TrayIcon(MainWindow window, SidebarViewModel vm)
    {
        _window = window;
        _vm = vm;
        _icon = new Forms.NotifyIcon { ContextMenuStrip = new Forms.ContextMenuStrip(), Visible = true };
        _icon.ContextMenuStrip.Opening += (_, _) => BuildMenu();
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                _window.ShowSidebar();
            }
        };
        _refresh.Tick += (_, _) => Update();
        _refresh.Start();
        _window.HiddenFromMenu += (_, _) => ShowHiddenHint();
        Update();
    }

    /// <summary>A notification by the icon: the sidebar is hidden, still running, and how to get it back.</summary>
    public void ShowHiddenHint() => _icon.ShowBalloonTip(6000, "Onething", TrayMenu.HiddenHint(_window.HasHotKey), Forms.ToolTipIcon.None);

    public void Dispose()
    {
        _refresh.Stop();
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _drawn?.Dispose();
    }

    private void Update()
    {
        _icon.Text = TrayMenu.Tooltip(_vm);
        if (_drawnAttention != _vm.HasAttention)
        {
            var previous = _drawn;
            _drawn = Draw(_vm.HasAttention);
            _icon.Icon = _drawn;
            previous?.Dispose();
            _drawnAttention = _vm.HasAttention;
        }
    }

    private void BuildMenu()
    {
        var menu = _icon.ContextMenuStrip!;
        foreach (var old in menu.Items.Cast<Forms.ToolStripItem>().ToList())
        {
            old.Dispose();
        }

        menu.Items.Clear();
        foreach (var item in TrayMenu.Items(_vm, _window.IsSidebarShown))
        {
            if (item.Id is not { } id)
            {
                menu.Items.Add(new Forms.ToolStripSeparator());
                continue;
            }

            // & marks a menu shortcut there: a title like "R&D review" keeps its &.
            var entry = new Forms.ToolStripMenuItem(item.Label.Replace("&", "&&", StringComparison.Ordinal)) { Enabled = item.Enabled };
            entry.Click += (_, _) => Run(id);
            menu.Items.Add(entry);
        }
    }

    private void Run(string id)
    {
        switch (id)
        {
            case TrayMenu.Open:
                _vm.OpenDashboardCommand.Execute(null);
                break;
            case TrayMenu.Add:
                _window.FocusCapture();
                break;
            case TrayMenu.StartFocus:
                _vm.StartFocusCommand.Execute(_vm.Focus);
                break;
            case TrayMenu.PauseFocus:
                _vm.PausePomodoroCommand.Execute(null);
                break;
            case TrayMenu.ResumeFocus:
                _vm.ResumePomodoroCommand.Execute(null);
                break;
            case TrayMenu.StopTimer:
                _vm.StopTimerCommand.Execute(null);
                break;
            case TrayMenu.ToggleSidebar:
                if (_window.IsSidebarShown)
                {
                    _window.HideSidebar();
                }
                else
                {
                    _window.ShowSidebar();
                }

                break;
            case TrayMenu.Settings:
                _vm.OpenSettingsCommand.Execute(null);
                break;
            case TrayMenu.Quit:
                _window.Close();
                break;
        }
    }

    /// <summary>The app's mark (a check on the brand gradient), with a red dot when a reminder needs you.</summary>
    private static Drawing.Icon Draw(bool attention)
    {
        var size = Forms.SystemInformation.SmallIconSize.Width * 2;
        using var bitmap = new Drawing.Bitmap(size, size);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = size * 0.22f;
            using var shape = new Drawing.Drawing2D.GraphicsPath();
            shape.AddArc(0, 0, r * 2, r * 2, 180, 90);
            shape.AddArc(size - (r * 2) - 1, 0, r * 2, r * 2, 270, 90);
            shape.AddArc(size - (r * 2) - 1, size - (r * 2) - 1, r * 2, r * 2, 0, 90);
            shape.AddArc(0, size - (r * 2) - 1, r * 2, r * 2, 90, 90);
            shape.CloseFigure();
            using var brand = new Drawing.Drawing2D.LinearGradientBrush(new Drawing.Point(0, 0), new Drawing.Point(size, size), Drawing.Color.FromArgb(0x4F, 0x46, 0xE5), Drawing.Color.FromArgb(0xDB, 0x27, 0x77))
            {
                InterpolationColors = new Drawing.Drawing2D.ColorBlend
                {
                    Colors = [Drawing.Color.FromArgb(0x4F, 0x46, 0xE5), Drawing.Color.FromArgb(0x7C, 0x3A, 0xED), Drawing.Color.FromArgb(0xDB, 0x27, 0x77)],
                    Positions = [0f, 0.5f, 1f],
                },
            };
            g.FillPath(brand, shape);

            // One lit task (the thing to do now), then two faint ones: the same shapes as build/icons.py.
            void DrawTask(float y, float dotRadius, float barX, float barWidth, float barHeight, int alpha)
            {
                using var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(alpha, Drawing.Color.White));
                g.FillEllipse(fill, size * (0.314f - dotRadius), size * (y - dotRadius), size * dotRadius * 2, size * dotRadius * 2);
                using var bar = new Drawing.Drawing2D.GraphicsPath();
                var h = size * barHeight;
                var x = size * barX;
                var top = (size * y) - (h / 2);
                var w = size * barWidth;
                bar.AddArc(x, top, h, h, 90, 180);
                bar.AddArc(x + w - h, top, h, h, 270, 180);
                bar.CloseFigure();
                g.FillPath(fill, bar);
            }

            DrawTask(0.322f, 0.107f, 0.471f, 0.324f, 0.078f, 255);
            DrawTask(0.547f, 0.061f, 0.432f, 0.293f, 0.051f, 102);
            DrawTask(0.723f, 0.061f, 0.432f, 0.234f, 0.051f, 64);
            if (attention)
            {
                using var dot = new Drawing.SolidBrush(Drawing.Color.FromArgb(0xEF, 0x44, 0x44));
                using var ring = new Drawing.Pen(Drawing.Color.White, size * 0.05f);
                var d = size * 0.42f;
                g.FillEllipse(dot, size - d, 0, d - 1, d - 1);
                g.DrawEllipse(ring, size - d, 0, d - 1, d - 1);
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            // A managed copy, so the native handle can be freed right away.
            using var native = Drawing.Icon.FromHandle(handle);
            return (Drawing.Icon)native.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
