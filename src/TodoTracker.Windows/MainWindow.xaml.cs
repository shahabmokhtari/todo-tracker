using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TodoTracker.Desktop;
using TodoTracker.Server;

namespace TodoTracker.Windows;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Disposed when the window closes.")]
public partial class MainWindow : Window
{
    private const double FullWidth = 360;
    private const double CompactWidth = 56;
    private readonly SidebarViewModel _vm;
    private readonly SettingsStore? _settings;
    private readonly bool _dockOnStart;
    private readonly DesktopSidebarHost _appBar;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private GlobalHotKey? _hotKey;

    internal MainWindow(SidebarViewModel viewModel, SettingsStore? settings, bool dockOnStart)
    {
        InitializeComponent();
        _vm = viewModel;
        _settings = settings;
        _dockOnStart = dockOnStart;
        DataContext = viewModel;
        _appBar = new DesktopSidebarHost(this) { WidthInDips = FullWidth };
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            _vm.StatusMessage = null;
        };
        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) =>
        {
            _hotKey?.Dispose();
            _appBar.Dispose();
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        };
    }

    internal bool IsDocked => _appBar.IsDocked;

    internal void FocusCapture()
    {
        if (_vm.IsCompact)
        {
            _vm.IsCompact = false;
        }

        Activate();
        CaptureBox.Focus();
        Keyboard.Focus(CaptureBox);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (_dockOnStart)
        {
            _appBar.Dock();
            _hotKey = new GlobalHotKey(this, FocusCapture);
        }
        else
        {
            Left = SystemParameters.WorkArea.Right - FullWidth;
            Top = SystemParameters.WorkArea.Top;
            Height = SystemParameters.WorkArea.Height;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SidebarViewModel.IsCompact))
        {
            _appBar.WidthInDips = _vm.IsCompact ? CompactWidth : FullWidth;
            if (_appBar.IsDocked)
            {
                _appBar.Dock();
            }
            else
            {
                Width = _appBar.WidthInDips;
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
        menu.Items.Add(new MenuItem { Header = "Copy MCP config for Copilot / agents", Command = _vm.CopyMcpConfigCommand });
        menu.Items.Add(new MenuItem { Header = "Copy API token (browser extension)", Command = _vm.CopyApiTokenCommand });
        if (_settings is not null)
        {
            var teams = new MenuItem { Header = _settings.Current.TeamsWebhookUrl is null ? "Connect Teams reminders…" : "Teams reminders: connected (change…)" };
            teams.Click += (_, _) => ConfigureTeams();
            menu.Items.Add(teams);
        }

        menu.Items.Add(new Separator());
        var dock = new MenuItem { Header = "Reserve screen edge", IsCheckable = true, IsChecked = _appBar.IsDocked };
        dock.Click += (_, _) =>
        {
            if (_appBar.IsDocked)
            {
                _appBar.Undock();
            }
            else
            {
                _appBar.Dock();
            }
        };
        menu.Items.Add(dock);
        var startup = new MenuItem { Header = "Start with Windows", IsCheckable = true, IsChecked = StartupRegistration.IsEnabled };
        startup.Click += (_, _) => StartupRegistration.Set(startup.IsChecked);
        menu.Items.Add(startup);
        menu.Items.Add(new Separator());
        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => Close();
        menu.Items.Add(exit);
        menu.IsOpen = true;
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


