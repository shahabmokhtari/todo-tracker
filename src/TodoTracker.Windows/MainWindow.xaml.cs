using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;
using System.Windows.Threading;
using TodoTracker.Desktop;
using TodoTracker.Server;

namespace TodoTracker.Windows;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Disposed when the window closes.")]
public partial class MainWindow : Window
{
    private const double CompactWidth = 56;
    private const double HeaderHeight = 44;
    private readonly SidebarViewModel _vm;
    private readonly SettingsStore? _settings;
    private readonly WindowPlacementStore? _placementStore;
    private readonly bool _interactive;
    private readonly DesktopSidebarHost _appBar;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private GlobalHotKey? _hotKey;
    private bool _applying;
    private WindowPlacement? _preferred;

    /// <param name="placementStore">Where placement is remembered; null keeps it in memory only (smoke tests).</param>
    /// <param name="interactive">False for automated runs: no global hotkey.</param>
    internal MainWindow(SidebarViewModel viewModel, SettingsStore? settings, WindowPlacementStore? placementStore, WindowPlacement placement, bool interactive)
    {
        InitializeComponent();
        _vm = viewModel;
        _settings = settings;
        _placementStore = placementStore;
        _interactive = interactive;
        Placement = placement;
        DataContext = viewModel;
        _appBar = new DesktopSidebarHost(this);
        _appBar.DockFailed += (_, _) => _vm.StatusMessage = "Windows didn't allow docking here; floating for now.";
        _appBar.ShellRestarted += OnShellRestarted;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            _vm.StatusMessage = null;
        };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            RememberFloatingBounds();
        };
        SourceInitialized += OnSourceInitialized;
        LocationChanged += (_, _) => QueueSave();
        SizeChanged += (_, _) => QueueSave();
        Closing += (_, _) => RememberFloatingBounds();
        StateChanged += (_, _) =>
        {
            // Win+Up maximizes without WM_SYSCOMMAND; a floating sidebar stays a sidebar-sized window.
            if (Placement.Mode == PlacementMode.Floating && WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
            }
        };
        Closed += (_, _) =>
        {
            _hotKey?.Dispose();
            _appBar.Dispose();
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        };
    }

    internal WindowPlacement Placement { get; private set; }

    internal bool IsDocked => _appBar.IsDocked;

    internal void FocusCapture()
    {
        if (_vm.IsCompact)
        {
            _vm.IsCompact = false;
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        CaptureBox.Focus();
        Keyboard.Focus(CaptureBox);
    }

    /// <summary>Releases the reserved screen edge; safe from any thread (used by crash and exit handlers).</summary>
    internal void ReleaseScreenEdge() => _appBar.Undock();

    /// <summary>Moves the sidebar: dock to an edge (optionally of another monitor) or float as a normal window.</summary>
    internal void ApplyPlacement(WindowPlacement placement) => ApplyPlacement(placement, isFallbackRetry: false);

    private void ApplyPlacement(WindowPlacement placement, bool isFallbackRetry)
    {
        ArgumentNullException.ThrowIfNull(placement);

        // Carry the live floating bounds into the new placement (the debounced save may not have run yet), so
        // toggling "Always on top" or docking right after a move never snaps the window back to stale bounds.
        if (Placement.Mode == PlacementMode.Floating && IsLoaded && WindowState == WindowState.Normal && !_vm.IsCompact
            && placement.Floating == Placement.Floating)
        {
            placement = placement with { Floating = _appBar.WindowBoundsPixels() };
        }

        if (!isFallbackRetry)
        {
            // An explicit choice replaces any docking preference we were waiting to restore.
            _preferred = null;
        }

        _saveTimer.Stop();
        _applying = true;
        try
        {
            Placement = placement;
            _appBar.Edge = placement.Edge;
            _appBar.MonitorDeviceName = placement.Monitor;
            _appBar.WidthInDips = _vm.IsCompact ? CompactWidth : placement.DockWidth;

            if (placement.Mode == PlacementMode.Docked)
            {
                WindowChrome.SetWindowChrome(this, null);
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Normal;
                _appBar.BlockMaximize = false;
                if (_appBar.IsDocked)
                {
                    // Edge or monitor changed: re-register so the shell recomputes every work area.
                    _appBar.Undock();
                }

                _appBar.Dock();
                if (!_appBar.IsDocked)
                {
                    // Keep the user's choice: float for now and retry docking when Explorer is back.
                    _preferred = placement;
                    Placement = placement with { Mode = PlacementMode.Floating };
                    ApplyFloating(Placement);
                }
            }
            else
            {
                _appBar.Undock();
                ApplyFloating(placement);
            }

            Topmost = Placement.IsTopmost;

            // Chevrons point toward the edge the strip collapses to.
            var left = Placement.Mode == PlacementMode.Docked && Placement.Edge == DockEdge.Left;
            CollapseButton.Content = left ? "\uE76B" : "\uE76C";
            ExpandButton.Content = left ? "\uE76C" : "\uE76B";
        }
        finally
        {
            _applying = false;
        }

        Persist();
    }

    /// <summary>Saves the effective placement, or the docked preference while docking is temporarily unavailable.</summary>
    private void Persist() =>
        _placementStore?.Save(_preferred is { } preferred ? preferred with { Floating = Placement.Floating, AlwaysOnTop = Placement.AlwaysOnTop } : Placement);

    private void ApplyFloating(WindowPlacement placement)
    {
        // A caption strip over the header lets the window be dragged; borders resize it.
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = HeaderHeight,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            UseAeroCaptionButtons = false,
        });
        ResizeMode = ResizeMode.CanResize;
        _appBar.BlockMaximize = true;
        WindowState = WindowState.Normal;

        // Physical pixels throughout: one coordinate space across monitors, whatever their DPI.
        var monitors = DesktopSidebarHost.Monitors();
        if (monitors.Count == 0)
        {
            return;
        }

        var anchor = monitors.FirstOrDefault(m => string.Equals(m.DeviceName, placement.Monitor, StringComparison.OrdinalIgnoreCase)) ?? monitors[0];
        var bounds = placement.Floating ?? PlacementMath.DefaultFloating(anchor.WorkAreaPixels, placement.Edge, anchor.Scale);
        bounds = PlacementMath.EnsureVisible(bounds, monitors.Select(m => m.WorkAreaPixels).ToList());
        if (_vm.IsCompact)
        {
            bounds = bounds with { Width = Math.Round(CompactWidth * anchor.Scale) };
        }

        _appBar.MoveWindowPixels(bounds);
    }

    private void QueueSave()
    {
        if (_applying || Placement.Mode != PlacementMode.Floating || !IsLoaded)
        {
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void RememberFloatingBounds(bool evenIfCompact = false)
    {
        if (Placement.Mode != PlacementMode.Floating || WindowState != WindowState.Normal || (_vm.IsCompact && !evenIfCompact) || !IsLoaded)
        {
            return;
        }

        Placement = Placement with { Floating = _appBar.WindowBoundsPixels() };
        Persist();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _appBar.Attach();
        ApplyPlacement(Placement);
        if (_interactive)
        {
            _hotKey = new GlobalHotKey(this, FocusCapture);
        }
    }

    private void OnShellRestarted(object? sender, EventArgs e)
    {
        // Explorer forgot our AppBar (or wasn't ready when we tried): dock again where the user wants it.
        if (_preferred is { } preferred)
        {
            ApplyPlacement(preferred, isFallbackRetry: true);
        }
        else if (Placement.Mode == PlacementMode.Docked)
        {
            ApplyPlacement(Placement, isFallbackRetry: true);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SidebarViewModel.IsCompact))
        {
            if (Placement.Mode == PlacementMode.Docked)
            {
                _appBar.WidthInDips = _vm.IsCompact ? CompactWidth : Placement.DockWidth;
                _appBar.Dock();
            }
            else if (_vm.IsCompact)
            {
                // Remember the full-size bounds first, then shrink to the strip in place.
                RememberFloatingBounds(evenIfCompact: true);
                Width = CompactWidth;
            }
            else
            {
                ApplyFloating(Placement);
            }
        }
        else if (e.PropertyName == nameof(SidebarViewModel.StatusMessage) && _vm.StatusMessage is not null)
        {
            _statusTimer.Stop();
            _statusTimer.Start();
        }
    }

    private void OnCaptureKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _vm.CaptureCommand.Execute(null);
        }
        else if (e.Key == Key.Escape)
        {
            _vm.QuickText = string.Empty;
        }
    }

    private void OnNoteKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is FrameworkElement { DataContext: CardViewModel card })
        {
            e.Handled = true;
            _vm.AddNoteCommand.Execute(card);
        }
        else if (e.Key == Key.Escape && sender is FrameworkElement { DataContext: CardViewModel open })
        {
            open.IsNoteOpen = false;
        }
    }

    private void OnSnoozeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CardViewModel card } element)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = element };
        foreach (var option in _vm.SnoozeOptions)
        {
            menu.Items.Add(new MenuItem { Header = option.Label, Command = _vm.SnoozeCommand, CommandParameter = new SnoozeRequest(card, option) });
        }

        menu.IsOpen = true;
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CardViewModel card } element)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = element };
        menu.Items.Add(new MenuItem { Header = "Add subtask…", Command = _vm.AddSubtaskCommand, CommandParameter = card });
        menu.Items.Add(new MenuItem { Header = "Edit details in browser", Command = _vm.EditInBrowserCommand, CommandParameter = card });
        menu.Items.Add(new MenuItem { Header = "Open full report", Command = _vm.OpenReportCommand, CommandParameter = card });
        menu.IsOpen = true;
    }

    private void OnMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        menu.Items.Add(new MenuItem { Header = "Open dashboard", Command = _vm.OpenDashboardCommand });
        menu.Items.Add(new MenuItem { Header = "Full timeline", Command = _vm.OpenReportCommand });
        menu.Items.Add(new Separator());
        menu.Items.Add(PositionMenu());
        var onTop = new MenuItem
        {
            Header = "Always on top",
            IsCheckable = true,
            IsChecked = Placement.IsTopmost,
            IsEnabled = Placement.Mode == PlacementMode.Floating,
            ToolTip = Placement.Mode == PlacementMode.Docked ? "A docked sidebar is always on top." : null,
        };
        // Checked/Unchecked (not Click) so mouse, keyboard, and UI Automation toggles all apply the change.
        onTop.Checked += (_, _) => ApplyPlacement(Placement with { AlwaysOnTop = true });
        onTop.Unchecked += (_, _) => ApplyPlacement(Placement with { AlwaysOnTop = false });
        menu.Items.Add(onTop);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Copy MCP config for Copilot / agents", Command = _vm.CopyMcpConfigCommand });
        menu.Items.Add(new MenuItem { Header = "Copy API token (browser extension)", Command = _vm.CopyApiTokenCommand });
        if (_settings is not null)
        {
            var teams = new MenuItem { Header = _settings.Current.TeamsWebhookUrl is null ? "Connect Teams reminders…" : "Teams reminders: connected (change…)" };
            teams.Click += (_, _) => ConfigureTeams();
            menu.Items.Add(teams);
        }

        menu.Items.Add(new Separator());
        var startup = new MenuItem { Header = "Start with Windows", IsCheckable = true, IsChecked = StartupRegistration.IsEnabled };
        startup.Checked += (_, _) => StartupRegistration.Set(true);
        startup.Unchecked += (_, _) => StartupRegistration.Set(false);
        menu.Items.Add(startup);
        menu.Items.Add(new Separator());
        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => Close();
        menu.Items.Add(exit);
        menu.IsOpen = true;
    }

    private MenuItem PositionMenu()
    {
        var position = new MenuItem { Header = "Position" };
        MenuItem Option(string header, bool isChecked, WindowPlacement target)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
            item.Click += (_, _) => ApplyPlacement(target);
            return item;
        }

        var docked = Placement.Mode == PlacementMode.Docked;
        position.Items.Add(Option("Dock right", docked && Placement.Edge == DockEdge.Right, Placement with { Mode = PlacementMode.Docked, Edge = DockEdge.Right }));
        position.Items.Add(Option("Dock left", docked && Placement.Edge == DockEdge.Left, Placement with { Mode = PlacementMode.Docked, Edge = DockEdge.Left }));
        position.Items.Add(Option("Float as a window", !docked, Placement with { Mode = PlacementMode.Floating }));

        var monitors = DesktopSidebarHost.Monitors();
        if (monitors.Count > 1)
        {
            position.Items.Add(new Separator());
            var current = _appBar.TargetMonitor()?.DeviceName;
            for (var i = 0; i < monitors.Count; i++)
            {
                var m = monitors[i];
                var label = $"Display {i + 1}{(m.IsPrimary ? " (primary)" : string.Empty)} · {m.Bounds.Width}×{m.Bounds.Height}";
                var target = Placement with
                {
                    Monitor = m.DeviceName,
                    Floating = Placement.Mode == PlacementMode.Floating ? PlacementMath.DefaultFloating(m.WorkAreaPixels, Placement.Edge, m.Scale) : Placement.Floating,
                };
                position.Items.Add(Option(label, string.Equals(current, m.DeviceName, StringComparison.OrdinalIgnoreCase), target));
            }
        }

        return position;
    }

    private void ConfigureTeams()
    {
        var url = InputDialog.Show(
            this,
            "Teams reminders",
            "Paste a Teams Workflows webhook URL (Teams channel › Workflows › \"Post to a channel when a webhook request is received\"). Leave empty to disconnect.",
            null);
        if (url is null)
        {
            return;
        }

        try
        {
            _settings!.SetTeamsWebhook(url);
            _vm.StatusMessage = string.IsNullOrWhiteSpace(url) ? "Teams reminders disconnected" : "Teams reminders connected";
        }
        catch (ArgumentException ex)
        {
            _vm.StatusMessage = TodoTracker.Core.ErrorText.Friendly(ex);
        }
    }
}
