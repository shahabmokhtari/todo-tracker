using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using TodoTracker.Desktop;

namespace TodoTracker.Tray;

/// <summary>A small window to add a task (the same quick capture as everywhere: @tomorrow, !high, #tag).</summary>
internal sealed class QuickAddWindow : Window
{
    public QuickAddWindow(SidebarViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        Title = "Add a task";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        var box = new TextBox { PlaceholderText = "What needs doing? (@tomorrow, !high, #tag)", AcceptsReturn = false };
        var status = new TextBlock { Opacity = 0.7, FontSize = 12 };
        var add = new Button { Content = "Add", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        var busy = false;
        async void Add()
        {
            // Once per press: a second Enter while it's being added would add it twice.
            if (busy || string.IsNullOrWhiteSpace(box.Text))
            {
                return;
            }

            busy = true;
            add.IsEnabled = false;
            vm.QuickText = box.Text;
            try
            {
                await vm.CaptureCommand.ExecuteAsync(null).ConfigureAwait(true);
            }
            finally
            {
                busy = false;
                add.IsEnabled = true;
            }

            if (string.IsNullOrEmpty(vm.QuickText))
            {
                Close();
            }
            else
            {
                status.Text = vm.StatusMessage ?? "Couldn't add it.";
            }
        }

        add.Click += (_, _) => Add();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children = { box, status, add },
        };
        Opened += (_, _) => box.Focus();
    }
}
