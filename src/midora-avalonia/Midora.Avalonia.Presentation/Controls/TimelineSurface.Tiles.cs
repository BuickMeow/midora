using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Midora.Avalonia.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Avalonia.Presentation.Controls;

/// <summary>
/// Batched shape pass shared by every timeline mode. Primitives are queued as plain colours and
/// flushed once per layer, so a dense frame costs a single Skia submission per layer instead of
/// one drawing call per note, event, grid line or lane border.
/// </summary>
public sealed partial class TimelineSurface
{
    private readonly TimelineShapeBatch _shapeBatch = new();

    /// <summary>Queues a filled rectangle for the batched Skia shape pass.</summary>
    private void AddFill(IBrush brush, Rect rect, double radius = 0, double opacity = 1)
    {
        if (TryGetShapeColor(brush, out uint color, opacity))
        {
            _shapeBatch.AddRect(rect, color, null, radius);
        }
    }

    /// <summary>Queues an outlined (optionally filled) rectangle for the batched shape pass.</summary>
    private void AddShape(
        IBrush? fill,
        IBrush? stroke,
        Rect rect,
        double radius = 0,
        double opacity = 1)
    {
        uint fillColor = 0;
        uint strokeColor = 0;
        bool hasFill = fill is not null && TryGetShapeColor(fill, out fillColor, opacity);
        bool hasStroke = stroke is not null && TryGetShapeColor(stroke, out strokeColor, opacity);
        if (!hasFill && !hasStroke)
        {
            return;
        }

        _shapeBatch.AddRect(
            rect,
            hasFill ? fillColor : 0,
            hasStroke ? strokeColor : null,
            radius);
    }

    private void AddShape(
        IBrush? fill,
        IPen? stroke,
        Rect rect,
        double radius = 0,
        double opacity = 1) =>
        AddShape(fill, stroke?.Brush, rect, radius, opacity);

    /// <summary>Queues a line for the batched Skia shape pass.</summary>
    private void AddLine(IBrush brush, Point from, Point to, double width = 1)
    {
        if (TryGetShapeColor(brush, out uint color))
        {
            _shapeBatch.AddLine(from.X, from.Y, to.X, to.Y, color, width);
        }
    }

    private void AddLine(IPen? pen, Point from, Point to)
    {
        if (pen?.Brush is { } brush)
        {
            AddLine(brush, from, to, pen.Thickness);
        }
    }

    /// <summary>Queues an ellipse for the batched Skia shape pass.</summary>
    private void AddEllipse(IBrush? fill, IPen? stroke, Point center, double radiusX, double radiusY)
    {
        uint fillColor = 0;
        uint strokeColor = 0;
        bool hasFill = fill is not null && TryGetShapeColor(fill, out fillColor);
        bool hasStroke = stroke?.Brush is not null && TryGetShapeColor(stroke.Brush, out strokeColor);
        if (!hasFill && !hasStroke)
        {
            return;
        }

        _shapeBatch.AddEllipse(
            center.X,
            center.Y,
            radiusX,
            radiusY,
            hasFill ? fillColor : 0,
            hasStroke ? strokeColor : 0,
            hasStroke);
    }

    /// <summary>Executes the accumulated shape batch as one leased-Skia draw operation.</summary>
    private void FlushShapes(DrawingContext context)
    {
        if (_shapeBatch.Count == 0)
        {
            return;
        }

        context.Custom(new TimelineShapeDrawOperation(
            new Rect(Bounds.Size),
            _shapeBatch.Build()));
        _shapeBatch.Reset();
    }

    private static bool TryGetShapeColor(IBrush brush, out uint color, double opacity = 1)
    {
        if (brush is not ISolidColorBrush solid)
        {
            color = 0;
            return false;
        }

        opacity = Math.Clamp(brush.Opacity * opacity, 0, 1);
        byte alpha = (byte)Math.Clamp(Math.Round(solid.Color.A * opacity), 0, 255);
        color = ((uint)alpha << 24)
            | ((uint)solid.Color.R << 16)
            | ((uint)solid.Color.G << 8)
            | solid.Color.B;
        return alpha != 0;
    }

    private double GetRenderScaling() =>
        TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;

    private static uint ToArgb(global::Avalonia.Media.Color color) =>
        ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

    private static global::Avalonia.Media.Color FromArgb(uint argb) => global::Avalonia.Media.Color.FromArgb(
        (byte)(argb >> 24),
        (byte)(argb >> 16),
        (byte)(argb >> 8),
        (byte)argb);
}
