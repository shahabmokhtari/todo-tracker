using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using TodoTracker.Desktop;

namespace TodoTracker.Windows;

/// <summary>
/// The full-screen break after a focus session: a translucent window over every monitor (rebuilt when monitors come
/// and go), on top of everything, with "I'm taking it" and "Skip the break". It shows and hides with
/// <see cref="BreakScreenViewModel.IsShown"/>.
/// </summary>
internal sealed partial class BreakOverlay : IDisposable
{
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr TopMost = new(-1);
    private static readonly Color Scrim = Color.FromArgb(0xE0, 0x0B, 0x10, 0x20);
    private static readonly Color Accent = Color.FromRgb(0x63, 0x66, 0xF1);
    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(250);
    private readonly BreakScreenViewModel _vm;
    private readonly Func<bool> _enabled;
    private readonly List<Window> _windows = [];
    private DateTime _shownAt;
    private bool _closing;
    private bool _disposed;

    public BreakOverlay(BreakScreenViewModel vm, Func<bool> enabled)
    {
        _vm = vm;
        _enabled = enabled;
        _vm.PropertyChanged += OnChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
    }

    /// <summary>Shows or hides the windows to match the break (and the settings).</summary>
    public void Sync()
    {
        if (_disposed)
        {
            return;
        }

        if (_vm.IsShown && _enabled())
        {
            if (_windows.Count == 0)
            {
                Open();
            }
        }
        else
        {
            CloseAll();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _vm.PropertyChanged -= OnChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
        CloseAll();
    }

    private void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BreakScreenViewModel.IsShown))
        {
            Sync();
        }
    }

    // Raised on another thread. Monitors came or went: cover the screens there are now (or show a break that found
    // no monitor a moment ago).
    private void OnDisplaysChanged(object? sender, EventArgs e) => Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        CloseAll();
        Sync();
    });

    private void Open()
    {
        _shownAt = DateTime.UtcNow;
        var monitors = DesktopSidebarHost.Monitors();
        foreach (var monitor in monitors)
        {
            var window = Create(monitor);
            _windows.Add(window);
            window.Show();
        }

        // The main screen's card takes focus (not a button): a key typed elsewhere a moment ago can't answer it.
        if (_windows.FirstOrDefault() is { } first)
        {
            BringForward(new WindowInteropHelper(first).Handle);
            first.Activate();
            Keyboard.Focus((first.Content as Grid)?.Children.OfType<Border>().FirstOrDefault());
        }
    }

    /// <summary>
    /// Windows keeps a background app from taking the keyboard. The person asked for this screen (focus timer, full-screen
    /// breaks on), so it borrows the foreground app's input for a moment, the usual way to come forward.
    /// </summary>
    private static void BringForward(IntPtr handle)
    {
        var foreground = GetForegroundWindow();
        var theirs = GetWindowThreadProcessId(foreground, IntPtr.Zero);
        var ours = GetCurrentThreadId();
        if (theirs != 0 && theirs != ours && AttachThreadInput(ours, theirs, true))
        {
            SetForegroundWindow(handle);
            AttachThreadInput(ours, theirs, false);
        }
        else
        {
            SetForegroundWindow(handle);
        }
    }

    private void CloseAll()
    {
        // A window closing (Alt+F4, sign-out) can bring us here: never close one twice, and always finish.
        var windows = _windows.ToList();
        _windows.Clear();
        _closing = true;
        try
        {
            foreach (var window in windows)
            {
                window.Close();
            }
        }
        finally
        {
            _closing = false;
        }
    }

    private bool Settled => DateTime.UtcNow - _shownAt > BreakScreenViewModel.SettleTime;

    private Window Create(DisplayMonitor monitor)
    {
        var window = new Window
        {
            Title = "Time for a break",
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = new SolidColorBrush(Scrim),
            Topmost = true,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            ShowActivated = monitor.IsPrimary,
            WindowStartupLocation = WindowStartupLocation.Manual,
            DataContext = _vm,
            Content = Card(),
            Opacity = SystemParameters.ClientAreaAnimation ? 0 : 1,
        };
        System.Windows.Automation.AutomationProperties.SetName(window, "Time for a break");
        void Place() => SetWindowPos(new WindowInteropHelper(window).Handle, TopMost, monitor.Bounds.X, monitor.Bounds.Y, monitor.Bounds.Width, monitor.Bounds.Height, SwpNoActivate);
        window.SourceInitialized += (_, _) => Place();
        // Moving onto a monitor with another scale resizes the window: cover the whole monitor again.
        window.DpiChanged += (_, _) => window.Dispatcher.BeginInvoke(Place);
        window.Loaded += (_, _) =>
        {
            if (window.Opacity < 1)
            {
                window.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, FadeIn));
            }
        };
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (Settled)
                {
                    _vm.TakeBreakCommand.Execute(null);
                }
            }
        };
        // Alt+F4 means "I'm taking it" (the break itself goes on). The window stays; hiding happens after this close
        // attempt is over (closing a window from its own Closing throws).
        window.Closing += (_, e) =>
        {
            if (_closing || _disposed)
            {
                return;
            }

            e.Cancel = true;
            if (Settled)
            {
                window.Dispatcher.BeginInvoke(() => _vm.TakeBreakCommand.Execute(null));
            }
        };
        return window;
    }

    private Grid Card()
    {
        var white = Brushes.White;
        var soft = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));
        TextBlock Text(string? binding, double size, Brush brush, FontWeight weight, string? text = null)
        {
            var block = new TextBlock { FontSize = size, Foreground = brush, FontWeight = weight, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12), Text = text ?? string.Empty };
            if (binding is not null)
            {
                block.SetBinding(TextBlock.TextProperty, new Binding(binding));
            }

            return block;
        }

        var clock = Text(nameof(BreakScreenViewModel.TimeText), 76, white, FontWeights.SemiBold);
        clock.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
        System.Windows.Documents.Typography.SetNumeralAlignment(clock, FontNumeralAlignment.Tabular);
        // Its text (the time) is what's read out; the help text says what it is.
        System.Windows.Automation.AutomationProperties.SetHelpText(clock, "Break time left");

        Button Action(string label, Func<Task> run, bool primary)
        {
            var button = new Button
            {
                Content = label,
                MinWidth = 150,
                Padding = new Thickness(18, 10, 18, 10),
                Margin = new Thickness(6),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = primary ? white : soft,
                Background = primary ? new SolidColorBrush(Accent) : new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
            };
            button.Click += async (_, _) =>
            {
                if (Settled)
                {
                    await run().ConfigureAwait(true);
                }
            };
            return button;
        }

        var visible = new BooleanToVisibilityConverter();
        T Shown<T>(T element, string when)
            where T : FrameworkElement
        {
            element.SetBinding(UIElement.VisibilityProperty, new Binding(when) { Converter = visible });
            return element;
        }

        // During the break: take it, start the next focus now, or skip it. When it's over: start the next, or not now.
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(Shown(Action("I’m taking it", () => { _vm.TakeBreakCommand.Execute(null); return Task.CompletedTask; }, primary: true), nameof(BreakScreenViewModel.IsBreak)));
        actions.Children.Add(Shown(Action("▶ Start next focus now", () => _vm.StartNextFocusCommand.ExecuteAsync(null), primary: false), nameof(BreakScreenViewModel.IsBreak)));
        actions.Children.Add(Shown(Action("Skip the break", () => _vm.SkipBreakCommand.ExecuteAsync(null), primary: false), nameof(BreakScreenViewModel.IsBreak)));
        actions.Children.Add(Shown(Action("▶ Start next focus", () => _vm.StartNextFocusCommand.ExecuteAsync(null), primary: true), nameof(BreakScreenViewModel.IsOver)));
        actions.Children.Add(Shown(Action("Not now", () => { _vm.TakeBreakCommand.Execute(null); return Task.CompletedTask; }, primary: false), nameof(BreakScreenViewModel.IsOver)));

        var stack = new StackPanel { MaxWidth = 620, Margin = new Thickness(32) };
        stack.Children.Add(Shown(Text(null, 46, white, FontWeights.Normal, "☕"), nameof(BreakScreenViewModel.IsBreak)));
        stack.Children.Add(Shown(Text(null, 46, white, FontWeights.Normal, "🎯"), nameof(BreakScreenViewModel.IsOver)));
        stack.Children.Add(Text(nameof(BreakScreenViewModel.Title), 34, white, FontWeights.Bold));
        stack.Children.Add(Text(nameof(BreakScreenViewModel.Tip), 19, soft, FontWeights.Normal));
        stack.Children.Add(Shown(clock, nameof(BreakScreenViewModel.IsBreak)));
        stack.Children.Add(Shown(Text(null, 15, soft, FontWeights.Normal, "Your focus session is done. Step away; the timer tells you when to come back."), nameof(BreakScreenViewModel.IsBreak)));
        stack.Children.Add(Shown(Text(nameof(BreakScreenViewModel.NextText), 16, white, FontWeights.SemiBold), nameof(BreakScreenViewModel.HasNext)));
        stack.Children.Add(actions);

        var card = new Border { Child = stack, Focusable = true, FocusVisualStyle = null, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(card, "Time for a break");
        var grid = new Grid();
        grid.Children.Add(card);
        return grid;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);
}
