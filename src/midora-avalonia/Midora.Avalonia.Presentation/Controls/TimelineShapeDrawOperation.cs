using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace Midora.Avalonia.Presentation.Controls;

/// <summary>
/// P3 shape path: timeline shapes (lane bands, grid lines, Segment bodies, notes, velocity
/// bars, event points) are batched into one immutable command list and drawn by a single
/// leased Skia canvas instead of per-primitive Avalonia geometry. Text and tile images stay
/// on the normal drawing context so theme text and cached preview bitmaps are unaffected.
/// </summary>
internal sealed class TimelineShapeBatch
{
    private readonly List<TimelineShapeCommand> _commands = [];

    public int Count => _commands.Count;

    public void Reset() => _commands.Clear();

    public void AddRect(Rect rect, uint fill, uint? stroke = null, double radius = 0)
    {
        if (!rect.Width.Equals(0) && !rect.Height.Equals(0))
        {
            _commands.Add(new TimelineShapeCommand(
                TimelineShapeKind.Rect,
                rect,
                fill,
                stroke ?? 0,
                stroke.HasValue,
                radius));
        }
    }

    public void AddLine(double x1, double y1, double x2, double y2, uint color, double width)
    {
        _commands.Add(new TimelineShapeCommand(
            TimelineShapeKind.Line,
            default,
            color,
            0,
            false,
            width,
            x1,
            y1,
            x2,
            y2));
    }

    public void AddEllipse(double centerX, double centerY, double radiusX, double radiusY, uint fill, uint stroke, bool hasStroke)
    {
        _commands.Add(new TimelineShapeCommand(
            TimelineShapeKind.Ellipse,
            new Rect(centerX - radiusX, centerY - radiusY, radiusX * 2, radiusY * 2),
            fill,
            stroke,
            hasStroke,
            1));
    }

    public TimelineShapeCommand[] Build() => [.. _commands];

    public enum TimelineShapeKind : byte
    {
        Rect,
        Line,
        Ellipse,
    }

    public readonly record struct TimelineShapeCommand(
        TimelineShapeKind Kind,
        Rect Rect,
        uint Fill,
        uint Stroke,
        bool HasStroke,
        double Radius,
        double X1 = 0,
        double Y1 = 0,
        double X2 = 0,
        double Y2 = 0);
}

internal sealed class TimelineShapeDrawOperation : ICustomDrawOperation
{
    private readonly TimelineShapeBatch.TimelineShapeCommand[] _commands;

    public TimelineShapeDrawOperation(Rect bounds, TimelineShapeBatch.TimelineShapeCommand[] commands)
    {
        Bounds = bounds;
        _commands = commands;
    }

    public Rect Bounds { get; }

    public bool HitTest(Point p) => false;

    public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);

    public void Dispose()
    {
        // Immutable managed state only.
    }

    public void Render(ImmediateDrawingContext context)
    {
        ISkiaSharpApiLeaseFeature? feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (feature is null)
        {
            return;
        }

        using ISkiaSharpApiLease lease = feature.Lease();
        SKCanvas canvas = lease.SkCanvas;
        canvas.Save();
        canvas.ClipRect(new SKRect(
            (float)Bounds.X,
            (float)Bounds.Y,
            (float)Bounds.Right,
            (float)Bounds.Bottom));
        using SKPaint paint = new()
        {
            IsAntialias = false,
            Style = SKPaintStyle.Fill,
        };
        foreach (TimelineShapeBatch.TimelineShapeCommand command in _commands)
        {
            if (command.Kind == TimelineShapeBatch.TimelineShapeKind.Ellipse)
            {
                SKRect oval = new(
                    (float)command.Rect.X,
                    (float)command.Rect.Y,
                    (float)command.Rect.Right,
                    (float)command.Rect.Bottom);
                if (command.Fill != 0)
                {
                    paint.Style = SKPaintStyle.Fill;
                    paint.Color = ToSkColor(command.Fill);
                    canvas.DrawOval(oval, paint);
                }

                if (command.HasStroke)
                {
                    paint.Style = SKPaintStyle.Stroke;
                    paint.StrokeWidth = 1;
                    paint.Color = ToSkColor(command.Stroke);
                    canvas.DrawOval(oval, paint);
                }

                continue;
            }

            if (command.Kind == TimelineShapeBatch.TimelineShapeKind.Line)
            {
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = (float)command.Radius;
                paint.Color = ToSkColor(command.Fill);
                canvas.DrawLine(
                    (float)command.X1,
                    (float)command.Y1,
                    (float)command.X2,
                    (float)command.Y2,
                    paint);
                continue;
            }

            SKRect rect = new(
                (float)command.Rect.X,
                (float)command.Rect.Y,
                (float)command.Rect.Right,
                (float)command.Rect.Bottom);
            paint.Style = SKPaintStyle.Fill;
            paint.Color = ToSkColor(command.Fill);
            if (command.Fill == 0 && command.HasStroke)
            {
                paint.Style = SKPaintStyle.Stroke;
            }

            if (command.Radius > 0)
            {
                float radius = (float)command.Radius;
                if (command.HasStroke && command.Fill != 0)
                {
                    paint.Color = ToSkColor(command.Fill);
                    canvas.DrawRoundRect(rect, radius, radius, paint);
                    paint.Style = SKPaintStyle.Stroke;
                    paint.StrokeWidth = 1;
                    paint.Color = ToSkColor(command.Stroke);
                    canvas.DrawRoundRect(rect, radius, radius, paint);
                }
                else
                {
                    if (command.HasStroke)
                    {
                        paint.Style = SKPaintStyle.Stroke;
                        paint.StrokeWidth = 1;
                        paint.Color = ToSkColor(command.Stroke);
                    }

                    canvas.DrawRoundRect(rect, radius, radius, paint);
                }
            }
            else if (command.HasStroke && command.Fill != 0)
            {
                paint.Color = ToSkColor(command.Fill);
                canvas.DrawRect(rect, paint);
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = 1;
                paint.Color = ToSkColor(command.Stroke);
                canvas.DrawRect(rect, paint);
            }
            else
            {
                if (command.HasStroke)
                {
                    paint.Style = SKPaintStyle.Stroke;
                    paint.StrokeWidth = 1;
                    paint.Color = ToSkColor(command.Stroke);
                }

                canvas.DrawRect(rect, paint);
            }
        }

        canvas.Restore();
    }

    private static SKColor ToSkColor(uint argb) => new(
        (byte)(argb >> 16),
        (byte)(argb >> 8),
        (byte)argb,
        (byte)(argb >> 24));
}
