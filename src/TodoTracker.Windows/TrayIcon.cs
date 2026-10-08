using System.Windows.Threading;
using TodoTracker.Desktop;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace TodoTracker.Windows;

/// <summary>
/// Todo Tracker in the notification area: click to bring the sidebar back, right-click for the menu (open, add a
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
        Update();
    }

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

            var entry = new Forms.ToolStripMenuItem(item.Label) { Enabled = item.Enabled };
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
                _window.ShowSidebar();
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
            using var brand = new Drawing.Drawing2D.LinearGradientBrush(new Drawing.Point(0, 0), new Drawing.Point(size, size), Drawing.Color.FromArgb(0x63, 0x66, 0xF1), Drawing.Color.FromArgb(0xEC, 0x48, 0x99));
            g.FillPath(brand, shape);
            using var pen = new Drawing.Pen(Drawing.Color.White, size * 0.11f) { StartCap = Drawing.Drawing2D.LineCap.Round, EndCap = Drawing.Drawing2D.LineCap.Round, LineJoin = Drawing.Drawing2D.LineJoin.Round };
            g.DrawLines(pen, [new Drawing.PointF(size * 0.27f, size * 0.52f), new Drawing.PointF(size * 0.43f, size * 0.68f), new Drawing.PointF(size * 0.73f, size * 0.34f)]);
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
