using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
    private int _pendingPastes;
    private bool _notesFlushed;
    private bool _closed;
    private readonly SettingsStore? _settings;
    private readonly WindowPlacementStore? _placementStore;
    private readonly bool _interactive;
    private readonly DesktopSidebarHost _appBar;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private GlobalHotKey? _hotKey;
    private bool _applying;

    /// <summary>Where the tasks live (set by the app once the server is up).</summary>
    internal TodoTracker.Core.Vault.VaultBoardStore? Vault { get; set; }

    /// <summary>The app window (null in automated runs: the browser is used).</summary>
    internal AppWindowHost? AppHost { get; set; }

    /// <summary>Which features (plugins) are on; null: all.</summary>
    internal TodoTracker.Server.Plugins.PluginHost? Plugins { get; set; }

    internal TodoTracker.Server.Plugins.Sync.SyncService? Sync { get; set; }

    private bool On(string plugin) => Plugins?.IsRunning(plugin) ?? true;
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
        _appBar.DisplaysChanged += OnDisplaysChanged;
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
        InitPeek();
        LocationChanged += (_, _) => QueueSave();
        SizeChanged += (_, _) => QueueSave();
        Closing += OnClosing;
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
            _closed = true;
            _peekTimer.Stop();
            _hotKey?.Dispose();
            _appBar.Dispose();
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        };
    }

    internal WindowPlacement Placement { get; private set; }

    internal bool IsDocked => _appBar.IsDocked;

    internal void FocusCapture()
    {
        ShowSidebar();
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

    private void ApplyPlacement(WindowPlacement placement, bool isFallbackRetry, bool persist = true)
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
            _appBar.MonitorId = placement.Monitor;
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

        if (persist)
        {
            Persist();
        }
    }

    /// <summary>Only changes stacking: the window stays exactly where the user put it.</summary>
    private void SetAlwaysOnTop(bool value)
    {
        var live = Placement.Mode == PlacementMode.Floating && IsLoaded && WindowState == WindowState.Normal && !_vm.IsCompact;
        Placement = Placement with { AlwaysOnTop = value, Floating = live ? _appBar.WindowBoundsPixels() : Placement.Floating };
        Topmost = Placement.IsTopmost;
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

        var anchor = MonitorIdentity.Resolve(placement.Monitor, monitors) ?? monitors[0];
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
        // Restoring what was loaded: nothing new to save.
        ApplyPlacement(Placement, isFallbackRetry: false, persist: false);
        if (_interactive)
        {
            _hotKey = new GlobalHotKey(this, FocusCapture);
        }
    }

    /// <summary>
    /// Monitors were plugged in or out, rearranged, or changed resolution. Docked: dock on the saved monitor if it is
    /// connected (so the sidebar returns when it is plugged back in), else on the primary. The saved choice is kept.
    /// Floating: pull the window back on screen if its monitor went away.
    /// </summary>
    private void OnDisplaysChanged(object? sender, EventArgs e)
    {
        if (_hiddenByUser)
        {
            return;
        }

        if (_preferred is { } preferred)
        {
            // Docking failed earlier (e.g. every monitor was gone mid-change): try the user's choice again.
            ApplyPlacement(preferred, isFallbackRetry: true, persist: false);
        }
        else if (Placement.Mode == PlacementMode.Docked)
        {
            ApplyPlacement(Placement, isFallbackRetry: true, persist: false);
        }
        else if (WindowState == WindowState.Normal && IsLoaded)
        {
            var current = _appBar.WindowBoundsPixels();
            var visible = PlacementMath.EnsureVisible(current, DesktopSidebarHost.Monitors().Select(m => m.WorkAreaPixels).ToList());
            if (visible != current)
            {
                _appBar.MoveWindowPixels(visible);
            }
        }
    }

    private void OnShellRestarted(object? sender, EventArgs e)
    {
        if (_hiddenByUser)
        {
            return;
        }

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
            if (!_vm.IsCompact)
            {
                // Kept open while peeking ("Keep the sidebar open").
                RestoreAfterPeek();
            }

            if (Placement.Mode == PlacementMode.Docked)
            {
                _appBar.WidthInDips = _vm.IsCompact ? CompactWidth : Placement.DockWidth;
                if (!_hiddenByUser)
                {
                    _appBar.Dock();
                }
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

    /// <summary>Ctrl+V (or Shift+Insert) with a picture on the clipboard: attach it to the task and embed it in the note.</summary>
    private async void OnNotePreviewKeyDown(object sender, KeyEventArgs e)
    {
        var paste = (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control) || (e.Key == Key.Insert && Keyboard.Modifiers == ModifierKeys.Shift);
        if (!paste || sender is not TextBox { DataContext: CardViewModel card } box)
        {
            return;
        }

        // The clipboard belongs to other apps too: any of these calls can fail while one of them holds it.
        byte[] png;
        try
        {
            if (Clipboard.ContainsText() || !Clipboard.ContainsImage())
            {
                return;
            }

            e.Handled = true;
            if (Clipboard.GetImage() is not { } image)
            {
                _vm.StatusMessage = "Couldn't read the picture on the clipboard.";
                return;
            }

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            png = stream.ToArray();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            e.Handled = true;
            _vm.StatusMessage = "Couldn't read the picture on the clipboard.";
            return;
        }

        // Enter waits for the picture, so the note doesn't go without it.
        _pendingPastes++;
        try
        {
            // The box may show another task by now, and text may have been selected meanwhile: insert at the caret
            // of this task's box only, never in place of a selection.
            if (await _vm.AttachPastedImageAsync(card, png).ConfigureAwait(true) is { } embed && ReferenceEquals(box.DataContext, card))
            {
                var at = box.SelectionStart + box.SelectionLength;
                var text = (at > 0 && !char.IsWhiteSpace(box.Text[at - 1]) ? " " : string.Empty) + embed;
                box.Text = box.Text.Insert(at, text);
                box.CaretIndex = at + text.Length;
            }
        }
        finally
        {
            _pendingPastes--;
        }
    }

    private void OnNoteKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _pendingPastes > 0)
        {
            e.Handled = true;
            _vm.StatusMessage = "Still adding the picture – press Enter again in a moment.";
        }
        else if (e.Key == Key.Enter && sender is FrameworkElement { DataContext: CardViewModel card })
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
        if (Vault?.PathOf(card.Id) is { } rel)
        {
            var full = Path.GetFullPath(Path.Combine(Vault.RootPath, rel));
            if (On("obsidian"))
            {
                var obsidian = new MenuItem { Header = TodoTracker.Core.Vault.ObsidianVaults.IsInstalled() ? "Open in Obsidian" : "Get Obsidian (to open it there)…" };
                obsidian.Click += (_, _) => Open(TodoTracker.Core.Vault.ObsidianVaults.LinkOrDownload(TodoTracker.Core.Vault.ObsidianVaults.OpenUrl(full)));
                menu.Items.Add(obsidian);
            }

            var show = new MenuItem { Header = "Show the markdown file" };
            show.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"") { UseShellExecute = false });
            menu.Items.Add(show);
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(new MenuItem { Header = "Add subtask…", Command = _vm.AddSubtaskCommand, CommandParameter = card });
        menu.Items.Add(new MenuItem { Header = "Edit details in browser", Command = _vm.EditInBrowserCommand, CommandParameter = card });
        menu.Items.Add(new MenuItem { Header = "Open full report", Command = _vm.OpenReportCommand, CommandParameter = card });
        menu.IsOpen = true;
    }

    /// <summary>Light, dark or the system's: the same choice as Settings › Appearance (every window follows it).</summary>
    private static MenuItem ThemeMenu(SettingsStore settings)
    {
        var current = settings.Current.Theme ?? "system";
        var menu = new MenuItem { Header = "Theme" };
        foreach (var (value, label) in new[] { ("system", "Same as Windows"), ("light", "Light"), ("dark", "Dark") })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = current == value };
            item.Click += (_, _) => settings.SetTheme(value);
            menu.Items.Add(item);
        }

        return menu;
    }

    private void OnMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        menu.Items.Add(new MenuItem { Header = "Open the app", Command = _vm.OpenDashboardCommand, ToolTip = "Board, outline, reports and more, in their own window" });
        menu.Items.Add(new MenuItem { Header = "Settings…", Command = _vm.OpenSettingsCommand, ToolTip = "Appearance, connected apps (Notion, Microsoft To Do), sync and extras" });
        if (_settings is not null)
        {
            menu.Items.Add(ThemeMenu(_settings));
        }
        if (AppHost is { } appHost)
        {
            var inBrowser = new MenuItem { Header = "Open in the browser" };
            inBrowser.Click += (_, _) => appHost.OpenInBrowser("/");
            menu.Items.Add(inBrowser);
        }

        menu.Items.Add(new MenuItem { Header = "Full timeline", Command = _vm.OpenReportCommand });
        if (AppHost is { } host && _vm.ShowFocusTimer)
        {
            var breaks = new MenuItem { Header = "Full-screen breaks", IsCheckable = true, IsChecked = host.State.FullScreenBreaks, ToolTip = "When a focus session ends, cover every screen with a reminder to rest (you can skip it)." };
            breaks.Checked += (_, _) => host.SetFullScreenBreaks(true);
            breaks.Unchecked += (_, _) => host.SetFullScreenBreaks(false);
            menu.Items.Add(breaks);
        }
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
        onTop.Checked += (_, _) => SetAlwaysOnTop(true);
        onTop.Unchecked += (_, _) => SetAlwaysOnTop(false);
        menu.Items.Add(onTop);
        menu.Items.Add(new Separator());
        if (On("connect-ai"))
        {
            menu.Items.Add(new MenuItem { Header = "Connect an AI app (Claude, Copilot…)", Command = _vm.ConnectAiCommand });
        }

        menu.Items.Add(new MenuItem { Header = "Copy MCP config for Copilot / agents", Command = _vm.CopyMcpConfigCommand });
        if (On("browser-extension"))
        {
            menu.Items.Add(new MenuItem { Header = "Set up the browser extension (Edge, Chrome, Safari)…", Command = _vm.SetUpBrowserExtensionCommand });
        }

        menu.Items.Add(new MenuItem { Header = "Copy API token", Command = _vm.CopyApiTokenCommand });
        if (Vault is not null)
        {
            menu.Items.Add(StorageMenu());
        }

        if (Sync is not null)
        {
            menu.Items.Add(SyncMenu(Sync));
        }

        if (_settings is not null && On("teams"))
        {
            var teams = new MenuItem { Header = _settings.Current.TeamsWebhookUrl is null ? "Connect Teams reminders…" : "Teams reminders: connected (change…)" };
            teams.Click += (_, _) => ConfigureTeams();
            menu.Items.Add(teams);
        }

        if (Plugins is not null)
        {
            menu.Items.Add(PluginsMenu(Plugins));
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

    /// <summary>Sync with other computers: where, how it went, sync now, and the Sync panel (conflicts, provider).</summary>
    private MenuItem SyncMenu(TodoTracker.Server.Plugins.Sync.SyncService sync)
    {
        var view = sync.View;
        var state = view.State switch
        {
            "idle" => $"Synced with {view.Provider}",
            "syncing" => "Syncing…",
            "error" => "Couldn't sync",
            "off" => "Off",
            _ => "Nothing to sync with",
        };
        var item = new MenuItem { Header = view.Conflicts.Count > 0 ? $"Sync ({view.Conflicts.Count} to look at)" : "Sync", ToolTip = view.Problem ?? view.Where };
        item.Items.Add(new MenuItem { Header = state, IsEnabled = false });
        var now = new MenuItem { Header = "Sync now", IsEnabled = view.State is "idle" or "error" };
        now.Click += async (_, _) =>
        {
            // Off the UI thread: reading the folder and the cloud are partly synchronous.
            var after = await Task.Run(() => sync.SyncNowAsync()).ConfigureAwait(true);
            _vm.StatusMessage = after.Problem is { } problem ? $"Couldn't sync: {problem}" : $"Synced with {after.Provider}.";
        };
        item.Items.Add(now);
        item.Items.Add(new MenuItem { Header = "Sync settings and conflicts…", Command = _vm.OpenSyncCommand });
        return item;
    }

    /// <summary>Every optional feature is a plugin: switch it on or off (applies after a restart).</summary>
    private MenuItem PluginsMenu(TodoTracker.Server.Plugins.PluginHost host)
    {
        var plugins = new MenuItem { Header = "Plugins" };
        foreach (var (plugin, _) in host.Plugins)
        {
            var info = plugin.Info;
            var item = new MenuItem { Header = info.Name, ToolTip = info.Description, IsCheckable = true, IsChecked = host.Settings.IsEnabled(info) };
            item.Click += (_, _) =>
            {
                host.Settings.SetEnabled(info.Id, item.IsChecked);
                if (host.IsRunning(info.Id) != item.IsChecked
                    && MessageBox.Show(this, $"{info.Name} is turned {(item.IsChecked ? "on" : "off")} from the next start. Restart Todo Tracker now?", "Plugins", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    Restart();
                }
            };
            plugins.Items.Add(item);
        }

        return plugins;
    }

    /// <summary>Saves any unsaved note, then starts a new instance (it waits for this one to close) and exits.</summary>
    private async void Restart()
    {
        await _vm.FlushNotesAsync().ConfigureAwait(true);
        _notesFlushed = true;
        if (Environment.ProcessPath is { } exe)
        {
            ReleaseScreenEdge();
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, Arguments = "--restart" });
            Application.Current.Shutdown();
        }
    }

    /// <summary>Tasks are markdown files: open them, keep them in an Obsidian vault, or pick another folder.</summary>
    private MenuItem StorageMenu()
    {
        var root = Vault!.RootPath;
        var storage = new MenuItem { Header = "Tasks folder", ToolTip = root };
        var open = new MenuItem { Header = "Open the folder" };
        open.Click += (_, _) => Open(root);
        storage.Items.Add(new MenuItem { Header = root, IsEnabled = false });
        storage.Items.Add(open);
        if (On("obsidian"))
        {
            var obsidian = new MenuItem { Header = TodoTracker.Core.Vault.ObsidianVaults.IsInstalled() ? "Open in Obsidian" : "Get Obsidian (to open it there)…" };
            obsidian.Click += (_, _) => Open(TodoTracker.Core.Vault.ObsidianVaults.LinkOrDownload(TodoTracker.Core.Vault.ObsidianVaults.OpenUrl(root)));
            storage.Items.Add(obsidian);
        }

        storage.Items.Add(new Separator());

        var vaults = On("obsidian") ? TodoTracker.Core.Vault.ObsidianVaults.Discover() : [];
        if (vaults.Count > 0)
        {
            var use = new MenuItem { Header = "Keep tasks in an Obsidian vault" };
            foreach (var vault in vaults)
            {
                var target = TodoTracker.Core.Vault.ObsidianVaults.TaskFolderIn(vault);
                var item = new MenuItem { Header = vault.Name, ToolTip = target, IsCheckable = true, IsChecked = string.Equals(target, root, StringComparison.OrdinalIgnoreCase) };
                item.Click += (_, _) => SwitchVault(target);
                use.Items.Add(item);
            }

            storage.Items.Add(use);
        }

        var choose = new MenuItem { Header = "Choose another folder…" };
        choose.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Where should Todo Tracker keep your tasks?", InitialDirectory = root };
            if (dialog.ShowDialog(this) == true)
            {
                SwitchVault(dialog.FolderName);
            }
        };
        storage.Items.Add(choose);
        return storage;
    }

    /// <summary>Notes typed but not saved yet are saved before the window goes away.</summary>
    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        RememberFloatingBounds();
        if (_notesFlushed || !_vm.HasUnsavedNotes)
        {
            return;
        }

        // Close again once the notes are saved (never from inside this event: WPF forbids Close while closing).
        e.Cancel = true;
        _notesFlushed = true;
        await _vm.FlushNotesAsync().ConfigureAwait(true);
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!_closed)
            {
                Close();
            }
        });
    }

    private async void SwitchVault(string folder)
    {
        if (string.Equals(Path.GetFullPath(folder), Vault?.RootPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var message = $"Keep your tasks in\n{folder}?\n\nTodo Tracker restarts to switch. Your current tasks stay where they are (copy the group folders over if you want them there too).";
        var summary = TodoTracker.Core.Vault.VaultFolder.Inspect(folder);
        if (summary is { IsTaskFolder: false, Tasks: > 0 })
        {
            message += $"\n\nThis folder already has {summary.Tasks} notes in {summary.Groups} folders. Each folder will show as a tab and each note as a task. " +
                "Notes aren't changed unless you edit that task here.";
        }

        if (MessageBox.Show(this, message, "Tasks folder", MessageBoxButton.OKCancel, summary.Tasks > 0 && !summary.IsTaskFolder ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        _settings?.SetVaultPath(folder);
        Restart();
    }

    private static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Nothing registered for the link (e.g. Obsidian isn't installed).
        }
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
            var current = _appBar.TargetMonitor()?.Id;
            for (var i = 0; i < monitors.Count; i++)
            {
                var m = monitors[i];
                var label = MonitorIdentity.Label(i, m);
                var target = Placement with
                {
                    Monitor = m.Id,
                    Floating = Placement.Mode == PlacementMode.Floating ? PlacementMath.DefaultFloating(m.WorkAreaPixels, Placement.Edge, m.Scale) : Placement.Floating,
                };
                position.Items.Add(Option(label, string.Equals(current, m.Id, StringComparison.OrdinalIgnoreCase), target));
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
