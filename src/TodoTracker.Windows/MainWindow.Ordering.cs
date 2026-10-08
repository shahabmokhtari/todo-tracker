using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TodoTracker.Desktop;

namespace TodoTracker.Windows;

/// <summary>Reordering Do now in the sidebar: drag a card onto another (or onto the focus card), or Alt+↑/↓.</summary>
public partial class MainWindow
{
    private Point? _dragStart;
    private Border? _dropTarget;

    /// <summary>Another launch was started: show this sidebar instead (restore, activate, focus quick capture).</summary>
    internal void BringToFront()
    {
        // Hidden from the tray: shown again where it belongs (docked again, if it was).
        ShowSidebar();
        Focus();
    }

    private void OnCardMouseDown(object sender, MouseButtonEventArgs e) =>
        _dragStart = e.OriginalSource is DependencyObject source && IsInteractive(source, sender as DependencyObject) ? null : e.GetPosition(this);

    /// <summary>A press that ended without a drag must not start one later from its old position.</summary>
    private void OnWindowPreviewMouseUp(object sender, MouseButtonEventArgs e) => _dragStart = null;

    private void OnCardMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement { DataContext: CardViewModel { CanReorder: true } card } element)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragStart = null;
        element.Opacity = 0.5;
        try
        {
            DragDrop.DoDragDrop(element, new DataObject(typeof(CardViewModel), card), DragDropEffects.Move);
        }
        finally
        {
            element.Opacity = 1;
            ClearDropLine();
        }
    }

    private void OnCardDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (DropOn(sender, e) is { } drop)
        {
            e.Effects = DragDropEffects.Move;
            ShowDropLine(drop.Border, drop.After);
        }

        e.Handled = true;
    }

    private void OnCardDragLeave(object sender, DragEventArgs e) => ClearDropLine();

    private async void OnCardDrop(object sender, DragEventArgs e)
    {
        var drop = DropOn(sender, e);
        ClearDropLine();
        e.Handled = true;
        if (drop is { } d)
        {
            await _vm.MoveCardAsync(d.Card, d.Target, d.After).ConfigureAwait(true);
        }
    }

    /// <summary>Alt+↑ / Alt+↓ on a focused card moves it (the top one is the focus); typing in a note is left alone.</summary>
    private async void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.System || e.SystemKey is not (Key.Up or Key.Down) || Keyboard.FocusedElement is TextBox
            || Keyboard.FocusedElement is not FrameworkElement { DataContext: CardViewModel { CanReorder: true } card })
        {
            return;
        }

        e.Handled = true;
        var command = e.SystemKey == Key.Up ? _vm.MoveUpCommand : _vm.MoveDownCommand;
        await command.ExecuteAsync(card).ConfigureAwait(true);

        // The card may now live in another list (focus ↔ Do now): put the keyboard back on it to keep moving.
        _ = Dispatcher.BeginInvoke(() => FocusCard(card), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void FocusCard(CardViewModel card)
    {
        var stack = new Stack<DependencyObject>([this]);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is ButtonBase { IsVisible: true, Focusable: true } button && ReferenceEquals(button.DataContext, card))
            {
                button.Focus();
                return;
            }

            for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--)
            {
                stack.Push(VisualTreeHelper.GetChild(node, i));
            }
        }
    }

    private (CardViewModel Card, CardViewModel Target, Border Border, bool After)? DropOn(object sender, DragEventArgs e)
    {
        if (sender is not Border { DataContext: CardViewModel { CanReorder: true } target } border
            || e.Data.GetData(typeof(CardViewModel)) is not CardViewModel card || card.Id == target.Id)
        {
            return null;
        }

        // The focus card is the top: dropping on it always makes the dragged card the focus.
        var after = !ReferenceEquals(target, _vm.Focus) && e.GetPosition(border).Y > border.ActualHeight / 2;
        return (card, target, border, after);
    }

    private void ShowDropLine(Border border, bool after)
    {
        if (!ReferenceEquals(_dropTarget, border))
        {
            ClearDropLine();
        }

        _dropTarget = border;
        border.BorderBrush = (Brush)FindResource("Accent");
        border.BorderThickness = after ? new Thickness(0, 0, 0, 2) : new Thickness(0, 2, 0, 0);
    }

    private void ClearDropLine()
    {
        if (_dropTarget is { } border)
        {
            border.BorderThickness = new Thickness(0);
            _dropTarget = null;
        }
    }

    /// <summary>Clicks on buttons and text boxes inside a card are clicks, not the start of a drag.</summary>
    private static bool IsInteractive(DependencyObject source, DependencyObject? card)
    {
        for (var node = source; node is not null && !ReferenceEquals(node, card); node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is ButtonBase or TextBox)
            {
                return true;
            }
        }

        return false;
    }
}
