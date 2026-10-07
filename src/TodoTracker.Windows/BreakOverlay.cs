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
    private readonly BreakScreenViewModel _vm;
    private readonly Func<bool> _enabled;
    private readonly List<Window> _windows = [];
    private DateTime _shownAt;
    private bool _closing;

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

    private void OnDisplaysChanged(object? sender, EventArgs e) => Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        if (_windows.Count > 0)
        {
            CloseAll();
            Sync();
        }
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
            first.Activate();
            Keyboard.Focus((first.Content as Grid)?.Children.OfType<Border>().FirstOrDefault());
        }
    }

    private void CloseAll()
    {
        _closing = true;
        foreach (var window in _windows)
        {
            window.Close();
        }

        _windows.Clear();
        _closing = false;
    }

    /// <summary>Clicks and keys in the first moment are typing that was going on, not an answer.</summary>
    private bool Settled => DateTime.UtcNow - _shownAt > TimeSpan.FromMilliseconds(800);

    private Window Create(DisplayMonitor monitor)
    {
        var window = new Window
        {
            Title = "Time for a break",
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x0B, 0x10, 0x20)),
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
                window.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
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
        // Alt+F4 means "I'm taking it" (the break itself goes on).
        window.Closing += (_, e) =>
        {
            if (_closing)
            {
                return;
            }

            e.Cancel = true;
            if (Settled)
            {
                _vm.TakeBreakCommand.Execute(null);
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
        System.Windows.Automation.AutomationProperties.SetName(clock, "Break time left");

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
                Background = primary ? new SolidColorBrush(Color.FromRgb(0x63, 0x66, 0xF1)) : new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
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

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(Action("I’m taking it", () => { _vm.TakeBreakCommand.Execute(null); return Task.CompletedTask; }, primary: true));
        actions.Children.Add(Action("Skip the break", () => _vm.SkipBreakCommand.ExecuteAsync(null), primary: false));

        var stack = new StackPanel { MaxWidth = 620, Margin = new Thickness(32) };
        stack.Children.Add(Text(null, 46, white, FontWeights.Normal, "☕"));
        stack.Children.Add(Text(nameof(BreakScreenViewModel.Title), 34, white, FontWeights.Bold));
        stack.Children.Add(Text(nameof(BreakScreenViewModel.Tip), 19, soft, FontWeights.Normal));
        stack.Children.Add(clock);
        stack.Children.Add(Text(null, 15, soft, FontWeights.Normal, "Your focus session is done. Step away; the timer tells you when to come back."));
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
}
