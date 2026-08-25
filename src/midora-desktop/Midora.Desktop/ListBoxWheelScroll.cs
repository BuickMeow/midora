using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Midora.Desktop;

internal static class ListBoxWheelScroll
{
    public static void ScrollOneItemPerNotch(ListBox listBox, MouseWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(listBox);
        ArgumentNullException.ThrowIfNull(e);

        ScrollViewer? scrollViewer = FindVisualDescendant<ScrollViewer>(listBox);
        if (scrollViewer is null || scrollViewer.ScrollableHeight <= 0 || e.Delta == 0)
        {
            return;
        }

        int notchCount = Math.Max(
            1,
            (int)Math.Round(
                Math.Abs(e.Delta) / (double)Mouse.MouseWheelDeltaForOneLine,
                MidpointRounding.AwayFromZero));
        for (int index = 0; index < notchCount; index++)
        {
            if (e.Delta > 0)
            {
                scrollViewer.LineUp();
            }
            else
            {
                scrollViewer.LineDown();
            }
        }

        e.Handled = true;
    }

    private static T? FindVisualDescendant<T>(DependencyObject parent)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            T? nested = FindVisualDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
