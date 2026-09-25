using System.Windows;
using Microsoft.Toolkit.Uwp.Notifications;
using TodoTracker.Core;
using TodoTracker.Desktop;

namespace TodoTracker.Windows;

/// <summary>Native Windows toasts for reminders and focus-timer transitions, with Done / Snooze actions.</summary>
internal sealed class ToastService : IDisposable
{
    private readonly SidebarViewModel _viewModel;
    private readonly Window _window;

    public ToastService(SidebarViewModel viewModel, Window window)
    {
        _viewModel = viewModel;
        _window = window;
        ToastNotificationManagerCompat.OnActivated += OnActivated;
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

    public static void ShowFocus(PomodoroEvent evt) =>
        new ToastContentBuilder()
            .AddArgument("action", "focus")
            .AddText(evt.Kind == PomodoroEventKind.FocusCompleted ? "🍅 Focus session done" : "Break is over")
            .AddText(evt.Kind == PomodoroEventKind.FocusCompleted ? "Nice work. Stand up, stretch, drink some water." : "Ready for the next focus block? Pick one small next step.")
            .Show();

    public void Dispose() => ToastNotificationManagerCompat.OnActivated -= OnActivated;

    private void OnActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var args = ToastArguments.Parse(e.Argument);
        _window.Dispatcher.BeginInvoke(async () =>
        {
            _window.Activate();
            if (!args.TryGetValue("item", out var itemText) || !Guid.TryParse(itemText, out var itemId))
            {
                return;
            }

            Guid? reminderId = args.TryGetValue("reminder", out var r) && Guid.TryParse(r, out var rid) ? rid : null;
            var action = args.TryGetValue("action", out var a) && Enum.TryParse<ToastAction>(a, ignoreCase: true, out var parsed) ? parsed : ToastAction.Open;
            await _viewModel.HandleToastActionAsync(action, itemId, reminderId).ConfigureAwait(true);
        });
    }
}


