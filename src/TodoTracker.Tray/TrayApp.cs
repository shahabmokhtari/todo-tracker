using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using TodoTracker.Desktop;

namespace TodoTracker.Tray;

/// <summary>
/// Todo Tracker in the system tray: click it for the full window (in the browser); its menu adds a task, runs the
/// focus timer and quits. The icon says what's going on (a red dot: a reminder is due).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The tray icon lives as long as the app; Avalonia disposes the icons it set when the app shuts down.")]
internal sealed class TrayApp : Application
{
    private TrayHost? _host;
    private TrayIcon? _icon;
    private QuickAddWindow? _quickAdd;
    private bool? _drawnAttention;

    public TrayArgs Args { get; set; } = new();

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.UIThread.Post(async () => await StartAsync(desktop).ConfigureAwait(true));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _host = await TrayHost.StartAsync(TrayHost.Options(Args), new TrayShell()).ConfigureAwait(true);
        if (_host is null)
        {
            // Already running: its window was opened instead.
            desktop.Shutdown();
            return;
        }

        var menu = new NativeMenu();
        menu.Opening += (_, _) => Build(menu);
        Build(menu);
        _icon = new TrayIcon { Menu = menu, IsVisible = true };
        _icon.Clicked += (_, _) => _host.ViewModel.OpenDashboardCommand.Execute(null);
        TrayIcon.SetIcons(this, [_icon]);
        Update();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => Update();
        timer.Start();
    }

    private void Update()
    {
        if (_host is null || _icon is null)
        {
            return;
        }

        var vm = _host.ViewModel;
        _icon.ToolTipText = TrayMenu.Tooltip(vm);
        if (_drawnAttention != vm.HasAttention)
        {
            _icon.Icon = new WindowIcon(TrayArt.Draw(vm.HasAttention));
            _drawnAttention = vm.HasAttention;
        }
    }

    private void Build(NativeMenu menu)
    {
        if (_host is null)
        {
            return;
        }

        menu.Items.Clear();
        foreach (var item in TrayMenu.Items(_host.ViewModel, sidebarShown: null))
        {
            if (item.Id is not { } id)
            {
                menu.Items.Add(new NativeMenuItemSeparator());
                continue;
            }

            var entry = new NativeMenuItem(item.Label) { IsEnabled = item.Enabled };
            entry.Click += (_, _) => Run(id);
            menu.Items.Add(entry);
        }
    }

    private void Run(string id)
    {
        var vm = _host!.ViewModel;
        switch (id)
        {
            case TrayMenu.Open:
                vm.OpenDashboardCommand.Execute(null);
                break;
            case TrayMenu.Add:
                _quickAdd ??= new QuickAddWindow(vm);
                _quickAdd.Closed += (_, _) => _quickAdd = null;
                _quickAdd.Show();
                _quickAdd.Activate();
                break;
            case TrayMenu.StartFocus:
                vm.StartFocusCommand.Execute(vm.Focus);
                break;
            case TrayMenu.PauseFocus:
                vm.PausePomodoroCommand.Execute(null);
                break;
            case TrayMenu.ResumeFocus:
                vm.ResumePomodoroCommand.Execute(null);
                break;
            case TrayMenu.StopTimer:
                vm.StopTimerCommand.Execute(null);
                break;
            case TrayMenu.Settings:
                vm.OpenSettingsCommand.Execute(null);
                break;
            case TrayMenu.Quit:
                _ = QuitAsync();
                break;
        }
    }

    private async Task QuitAsync()
    {
        if (_icon is not null)
        {
            _icon.IsVisible = false;
        }

        if (_host is not null)
        {
            await _host.DisposeAsync().ConfigureAwait(true);
        }

        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
