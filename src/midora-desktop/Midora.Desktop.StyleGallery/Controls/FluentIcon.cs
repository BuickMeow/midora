using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Midora.Desktop.StyleGallery.Controls;

public sealed class FluentIcon : Control
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data),
        typeof(Geometry),
        typeof(FluentIcon),
        new FrameworkPropertyMetadata(null));

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }
}
