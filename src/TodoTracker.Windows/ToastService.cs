using System.Windows;
using Microsoft.Toolkit.Uwp.Notifications;
using TodoTracker.Core;
using TodoTracker.Desktop;
using TodoTracker.Server.Hooks;

namespace TodoTracker.Windows;

/// <summary>
/// Native Windows toasts for reminders and focus-timer transitions, with Done / Snooze actions; and for hooks an AI
/// agent added or changed: Allow (it runs from then on, without asking again) or Don't allow (it's removed).
/// </summary>
internal sealed class ToastService : IDisposable
{
    private const string HookOpen = "hookOpen";
    private const string HookAllow = "hookAllow";
    private const string HookDeny = "hookDeny";

    private readonly SidebarViewModel _viewModel;
    private readonly Window _window;
    private readonly HookStore? _hooks;
    private readonly Action<string>? _openApp;

    public ToastService(SidebarViewModel viewModel, Window window, HookStore? hooks = null, Action<string>? openApp = null)
    {
        _viewModel = viewModel;
        _window = window;
        _hooks = hooks;
        _openApp = openApp;
        ToastNotificationManagerCompat.OnActivated += OnActivated;
    }

    /// <summary>
    /// "Claude wants to run a hook: on taskCompleted, run …". Allow is offered only when the notification shows all of
    /// it (a short plain command, nothing else set: <see cref="HookEntry.Summary"/>); otherwise Review opens it whole
    /// in Settings › Hooks. Allowing or refusing names this version: a hook changed since is neither.
    /// </summary>
    public static void ShowHookApproval(HookEntry entry)
    {
        var hook = entry.Hook;
        var (what, complete) = entry.Summary();
        var builder = new ToastContentBuilder()
            .AddArgument("action", HookOpen)
            .AddArgument("hook", entry.Id)
            .AddArgument("version", entry.Version)
            .AddText($"🔌 {hook.AddedBy ?? "An AI agent"} wants to run a hook")
            .AddText($"On {entry.Event}: {what}")
            .AddAttributionText(complete ? "Allow it and it runs from now on" : "There's more to it: review all of it first");
        builder = complete
            ? builder.AddButton(new ToastButton().SetContent("Allow").AddArgument("action", HookAllow))
            : builder.AddButton(new ToastButton().SetContent("Review…").AddArgument("action", HookOpen));
        builder
            .AddButton(new ToastButton().SetContent("Don't allow").AddArgument("action", HookDeny))
            .Show(t =>
            {
                t.Tag = entry.Id;
                t.Group = "hooks";
            });
    }

    /// <summary>Whether notifications can show at all (switched off for the app or by policy: the app's page asks instead).</summary>
    public static bool CanShow()
    {
        try
        {
            return ToastNotificationManagerCompat.CreateToastNotifier().Setting == global::Windows.UI.Notifications.NotificationSetting.Enabled;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void ShowReminder(ReminderNotification n)
    {
        var builder = new ToastContentBuilder()
            .AddArgument("item", n.ItemId.ToString())
            .AddArgument("reminder", n.ReminderId.ToString())
            .AddArgument("action", nameof(ToastAction.Open))
            .AddText($"⏰ {n.Title}")
            .AddText(n.Message);
        if (n.Breadcrumb.Count > 0)
        {
            builder.AddAttributionText(string.Join(" › ", n.Breadcrumb));
        }

        builder
            .AddButton(new ToastButton().SetContent("✓ Done").AddArgument("action", nameof(ToastAction.Done)))
            .AddButton(new ToastButton().SetContent("Snooze 1h").AddArgument("action", nameof(ToastAction.Snooze)))
            .Show(t =>
            {
                t.Tag = n.ReminderId.ToString("N")[..16];
                t.Group = "reminders";
            });
    }

    public static void ShowFocus(PomodoroEvent evt)
    {
        var done = evt.Kind == PomodoroEventKind.FocusCompleted;
        new ToastContentBuilder()
            .AddArgument("action", "focus")
            .AddText(done ? "🍅 Focus session done" : "Break is over")
            .AddText(done ? "Nice work. Stand up, stretch, drink some water." : "Ready for the next focus block? Pick one small next step.")
            // The next session from here too (after the break; or right away, skipping it).
            .AddButton(new ToastButton().SetContent(done ? "Skip the break, start the next" : "▶ Start next focus").AddArgument("action", nameof(ToastAction.NextFocus)))
            .Show();
    }

    public void Dispose() => ToastNotificationManagerCompat.OnActivated -= OnActivated;

    /// <summary>Allow (only the version shown), Don't allow (removed, if it still waits), or see it in Settings › Hooks.</summary>
    private void OnHookDecision(string action, string? id, string? version)
    {
        if (_hooks is null || id is null)
        {
            return;
        }

        try
        {
            switch (action)
            {
                case HookAllow:
                    _hooks.Approve(id, version ?? string.Empty, HookAuthor.You);
                    _viewModel.StatusMessage = "Hook allowed: it runs from now on.";
                    break;
                case HookDeny:
                    _viewModel.StatusMessage = _hooks.Reject(id, version ?? string.Empty)
                        ? "Hook not allowed: it was removed."
                        : "That hook was allowed or changed meanwhile: it was kept (see Settings › Hooks).";
                    break;
                default:
                    _openApp?.Invoke("/#/settings/hooks");
                    break;
            }
        }
        catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException)
        {
            _viewModel.StatusMessage = "That hook is gone.";
        }
        catch (InvalidOperationException ex)
        {
            // Changed since the toast (or hooks.json can't be read): look at it again before allowing.
            _viewModel.StatusMessage = ex.Message;
            _openApp?.Invoke("/#/settings/hooks");
        }
    }

    private void OnActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var args = ToastArguments.Parse(e.Argument);
        if (args.TryGetValue("action", out var hookAction) && hookAction is HookOpen or HookAllow or HookDeny)
        {
            _window.Dispatcher.BeginInvoke(() => OnHookDecision(hookAction, args.TryGetValue("hook", out var id) ? id : null, args.TryGetValue("version", out var v) ? v : null));
            return;
        }

        _window.Dispatcher.BeginInvoke(async () =>
        {
            if (_window is MainWindow sidebar)
            {
                sidebar.ShowSidebar();
            }
            else
            {
                _window.Activate();
            }
            // The action first: the focus timer's buttons aren't about a task.
            var action = args.TryGetValue("action", out var a) && Enum.TryParse<ToastAction>(a, ignoreCase: true, out var parsed) ? parsed : ToastAction.Open;
            Guid? itemId = args.TryGetValue("item", out var itemText) && Guid.TryParse(itemText, out var id) ? id : null;
            Guid? reminderId = args.TryGetValue("reminder", out var r) && Guid.TryParse(r, out var rid) ? rid : null;
            await _viewModel.HandleToastActionAsync(action, itemId, reminderId).ConfigureAwait(true);
        });
    }
}


