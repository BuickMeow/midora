using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace Midora.Avalonia.Windows;

/// <summary>
/// Placeholder for the Midora.Application timeline value trace shape enum until the shared
/// domain types are referenced by the Avalonia port.
/// </summary>
public enum ValueTraceShape
{
    Free,
    Line,
    Horizontal
}

public partial class ValueTraceShapeSelector : UserControl
{
    public static readonly StyledProperty<ValueTraceShape> ShapeProperty =
        AvaloniaProperty.Register<ValueTraceShapeSelector, ValueTraceShape>(
            nameof(Shape),
            ValueTraceShape.Free,
            defaultBindingMode: BindingMode.TwoWay);

    public ValueTraceShapeSelector()
    {
        InitializeComponent();
        Refresh();
    }

    public ValueTraceShape Shape
    {
        get => GetValue(ShapeProperty);
        set => SetValue(ShapeProperty, value);
    }

    public event EventHandler? ShapeChosen;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShapeProperty)
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        if (Free is null) return;
        Free.IsChecked = Shape == ValueTraceShape.Free;
        Line.IsChecked = Shape == ValueTraceShape.Line;
        Horizontal.IsChecked = Shape == ValueTraceShape.Horizontal;
    }

    private void OnShapeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string tag }
            && Enum.TryParse(tag, out ValueTraceShape shape))
        {
            Shape = shape;
        }
        Refresh();
        ShapeChosen?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}

/// <summary>
/// Demo-only host that lets the selector be opened and reviewed outside MainWindow.
/// </summary>
public sealed class ValueTraceShapeSelectorPreviewWindow : Window
{
    public ValueTraceShapeSelectorPreviewWindow()
    {
        Title = "Value Trace Shape Selector";
        Width = 300;
        Height = 120;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new Border
        {
            Padding = new Thickness(16),
            Child = new ValueTraceShapeSelector
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }
}
