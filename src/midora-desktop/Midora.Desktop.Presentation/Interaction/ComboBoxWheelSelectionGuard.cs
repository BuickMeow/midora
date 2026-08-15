using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Midora.Desktop.Presentation.Interaction;

public static class ComboBoxWheelSelectionGuard
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(ComboBoxWheelSelectionGuard),
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
}
