using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Midora.Avalonia.Controls;

/// <summary>
/// Minimal Avalonia port of the approved WPF <c>ui:FluentIcon</c> control. Inherits
/// <see cref="TemplatedControl.Foreground"/> so button foregrounds propagate, and draws the
/// icon geometry uniformly scaled into the control bounds (the WPF Viewbox + Path behavior).
/// </summary>
public sealed class FluentIcon : TemplatedControl
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<FluentIcon, Geometry?>(nameof(Data));

    static FluentIcon()
    {
        AffectsRender<FluentIcon>(DataProperty, ForegroundProperty);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var geometry = Data;
        var foreground = Foreground;
        if (geometry is null || foreground is null)
        {
            return;
        }

        var bounds = geometry.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var scale = Math.Min(Bounds.Width / bounds.Width, Bounds.Height / bounds.Height);
        if (scale <= 0)
        {
            return;
        }

        var offsetX = (Bounds.Width - bounds.Width * scale) / 2 - bounds.X * scale;
        var offsetY = (Bounds.Height - bounds.Height * scale) / 2 - bounds.Y * scale;

        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY)))
        {
            context.DrawGeometry(foreground, null, geometry);
        }
    }
}
