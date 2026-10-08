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
    private NativeMenu? _menu;
    private QuickAddWindow? _quickAdd;
    private bool? _drawnAttention;
    private List<TrayItem> _built = [];
    private bool _quitting;

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
        var options = TrayHost.Options(Args);
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log(options.DataDirectory, e.Exception);
            e.Handled = true;
        };
        try
        {
            _host = await TrayHost.StartAsync(options, new TrayShell()).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Last chance at startup: say what went wrong instead of vanishing.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log(options.DataDirectory, ex);
            var problem = ex is IOException ? $"Port {options.Port} is in use (is another program, or another user's Todo Tracker, using it?).\n\n{ex.Message}" : ex.Message;
            var window = new ErrorWindow("Todo Tracker couldn't start", problem);
            window.Closed += (_, _) => desktop.Shutdown(1);
            window.Show();
            return;
        }

        if (_host is null)
        {
            // Already running: its window was opened instead.
            desktop.Shutdown();
            return;
        }

        // Ctrl+C, kill, logging out: quit properly (notes saved, the server stopped).
        _host.Stopping.Register(() => Dispatcher.UIThread.Post(() => _ = QuitAsync()));
        _menu = new NativeMenu();
        _menu.Opening += (_, _) => Build(force: true);
        Build(force: true);
        _icon = new TrayIcon { Menu = _menu, IsVisible = true };
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

        // Linux panels don't say when the menu opens: keep it current (only rebuilt when something changed).
        Build(force: false);
        if (_drawnAttention != vm.HasAttention)
        {
            _icon.Icon = new WindowIcon(TrayArt.Draw(vm.HasAttention));
            _drawnAttention = vm.HasAttention;
        }
    }

    private void Build(bool force)
    {
        if (_host is null || _menu is not { } menu)
        {
            return;
        }

        var items = TrayMenu.Items(_host.ViewModel, sidebarShown: null).ToList();
        if (!force && items.SequenceEqual(_built))
        {
            return;
        }

        if (items.Select(i => i.Id).SequenceEqual(_built.Select(i => i.Id)) && menu.Items.Count == items.Count)
        {
            // Only the words changed (the time left): updated in place, so an open menu stays open.
            for (var i = 0; i < items.Count; i++)
            {
                if (menu.Items[i] is NativeMenuItem existing && items[i].Id is not null)
                {
                    existing.Header = items[i].Label;
                    existing.IsEnabled = items[i].Enabled;
                }
            }

            _built = items;
            return;
        }

        _built = items;
        menu.Items.Clear();
        foreach (var item in items)
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
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        try
        {
            if (_icon is not null)
            {
                _icon.IsVisible = false;
            }

            if (_host is not null)
            {
                await _host.DisposeAsync().ConfigureAwait(true);
            }
        }
#pragma warning disable CA1031 // Quitting goes on whatever happened (it's written down).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log(TrayHost.Options(Args).DataDirectory, ex);
        }
        finally
        {
            (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
    }

    /// <summary>Problems go to errors.log in the data folder (best effort).</summary>
    private static void Log(string dataDirectory, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.AppendAllText(Path.Combine(dataDirectory, "errors.log"), $"{DateTimeOffset.Now:o} {ex}{Environment.NewLine}");
        }
        catch (Exception logging) when (logging is IOException or UnauthorizedAccessException)
        {
            // Nowhere to write it.
        }
    }
}
