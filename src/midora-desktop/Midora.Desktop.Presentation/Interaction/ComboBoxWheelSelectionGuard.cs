using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Midora.Desktop.Presentation.Interaction;

public static class ComboBoxWheelSelectionGuard
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(ComboBoxWheelSelectionGuard),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty UseSingleStepDropDownWheelProperty =
        DependencyProperty.RegisterAttached(
            "UseSingleStepDropDownWheel",
            typeof(bool),
            typeof(ComboBoxWheelSelectionGuard),
            new PropertyMetadata(false, OnUseSingleStepDropDownWheelChanged));

    private static readonly MouseWheelEventHandler PreviewMouseWheelHandler = OnPreviewMouseWheel;
    private static readonly MouseWheelEventHandler DropDownPreviewMouseWheelHandler =
        OnDropDownPreviewMouseWheel;

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

    public static bool GetUseSingleStepDropDownWheel(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(UseSingleStepDropDownWheelProperty);
    }

    public static void SetUseSingleStepDropDownWheel(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(UseSingleStepDropDownWheelProperty, value);
    }

    private static void OnIsEnabledChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not ComboBox comboBox)
        {
            return;
        }

        comboBox.RemoveHandler(UIElement.PreviewMouseWheelEvent, PreviewMouseWheelHandler);
        if (args.NewValue is true)
        {
            comboBox.AddHandler(
                UIElement.PreviewMouseWheelEvent,
                PreviewMouseWheelHandler,
                handledEventsToo: true);
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs args)
    {
        if (sender is ComboBox { IsDropDownOpen: false })
        {
            args.Handled = true;
        }
    }

    private static void OnUseSingleStepDropDownWheelChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not UIElement element) return;
        element.RemoveHandler(
            UIElement.PreviewMouseWheelEvent,
            DropDownPreviewMouseWheelHandler);
        if (args.NewValue is true)
        {
            element.AddHandler(
                UIElement.PreviewMouseWheelEvent,
                DropDownPreviewMouseWheelHandler,
                handledEventsToo: true);
        }
    }

    private static void OnDropDownPreviewMouseWheel(
        object sender,
        MouseWheelEventArgs args)
    {
        if (sender is not DependencyObject owner || args.Delta == 0) return;
        ScrollViewer? viewer = owner as ScrollViewer ?? FindDescendantScrollViewer(owner);
        if (viewer is not null)
        {
            if (args.Delta > 0) viewer.LineUp();
            else viewer.LineDown();
        }

        // An open drop-down owns the gesture even when its items fit without a
        // scrollbar or the wheel points beyond the current scroll boundary.
        args.Handled = true;
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject owner)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(owner);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(owner, index);
            if (child is ScrollViewer viewer) return viewer;
            if (FindDescendantScrollViewer(child) is { } descendant) return descendant;
        }
        return null;
    }
}
