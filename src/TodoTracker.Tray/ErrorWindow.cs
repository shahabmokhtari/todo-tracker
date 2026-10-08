using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TodoTracker.Tray;

/// <summary>Says why Todo Tracker couldn't start (instead of vanishing); closing it quits.</summary>
internal sealed class ErrorWindow : Window
{
    public ErrorWindow(string title, string message)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "Details are in errors.log in Todo Tracker's data folder.", Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                close,
            },
        };
    }
}
