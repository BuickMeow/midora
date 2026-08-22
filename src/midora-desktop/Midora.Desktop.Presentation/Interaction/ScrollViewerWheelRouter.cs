using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Midora.Desktop.Presentation.Interaction;

/// <summary>
/// Gives the nearest vertically scrollable <see cref="ScrollViewer"/> first use of
/// a mouse-wheel gesture, even when the pointer is over a TextBox, closed ComboBox,
/// or another non-scrolling input control.
/// </summary>
public static class ScrollViewerWheelRouter
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(ScrollViewerWheelRouter),
        new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly MouseWheelEventHandler PreviewMouseWheelHandler = OnPreviewMouseWheel;

    public static bool GetIsEnabled(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsEnabledProperty);
    }

    public static void SetIsEnabled(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsEnabledProperty, value);
    }

    private static void OnIsEnabledChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not ScrollViewer viewer) return;
        viewer.RemoveHandler(UIElement.PreviewMouseWheelEvent, PreviewMouseWheelHandler);
        if (args.NewValue is true)
        {
            viewer.AddHandler(
                UIElement.PreviewMouseWheelEvent,
                PreviewMouseWheelHandler,
                handledEventsToo: true);
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs args)
    {
        if (sender is not ScrollViewer viewer
            || args.Handled
            || viewer.VerticalScrollBarVisibility is ScrollBarVisibility.Disabled
                or ScrollBarVisibility.Hidden
            || viewer.ScrollableHeight <= 0)
        {
            return;
        }

        ScrollViewer? nearest = FindNearestScrollViewer(args.OriginalSource as DependencyObject);
        if (nearest is not null
            && !ReferenceEquals(nearest, viewer)
            && nearest.VerticalScrollBarVisibility is not (ScrollBarVisibility.Disabled
                or ScrollBarVisibility.Hidden)
            && nearest.ScrollableHeight > 0)
        {
            return;
        }

        double oldOffset = viewer.VerticalOffset;
        double targetOffset = Math.Clamp(
            oldOffset - args.Delta / 3d,
            0,
            viewer.ScrollableHeight);
        if (targetOffset == oldOffset) return;
        viewer.ScrollToVerticalOffset(targetOffset);
        args.Handled = true;
    }

    private static ScrollViewer? FindNearestScrollViewer(DependencyObject? current)
    {
        while (current is not null)
        {
            if (current is ScrollViewer viewer) return viewer;
            current = current is Visual or Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}
