using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Midora.Avalonia.Presentation.Rendering;
using SkiaSharp;

namespace Midora.Avalonia.Presentation.Controls;

/// <summary>
/// Identity of one GPU vertex batch: the preview source, both content fingerprints, the mapped
/// rectangle size, the device pixel width and the two colors. Every input that can change a pixel
/// is part of the key, so a hit can never draw stale data.
/// </summary>
internal readonly record struct TimelineNoteBatchKey(
    object Source,
    bool HasNoteContent,
    bool HasEventContent,
    ulong NoteFingerprint,
    ulong EventFingerprint,
    double Width,
    double Height,
    double DevicePixel,
    uint NoteColor,
    uint EventColor);

/// <summary>
/// Immutable Skia vertex batches for one Segment preview. Vertices are stored in pixels relative
/// to the Segment's full rectangle origin, so horizontal panning only changes the drawing
/// translation and never rebuilds the data; zoom, lane height, device pixel or content changes
/// produce a new batch. Each batch stays below the 16-bit index limit (16k quads = 64k vertices).
/// </summary>
internal sealed class TimelineNoteVertexBatch
{
    public const int QuadsPerBatch = 16_000;

    private TimelineNoteVertexBatch(
        SKVertices[] noteBatches,
        SKVertices[] eventBatches,
        int noteCount,
        int eventCount,
        uint noteColor,
        uint eventColor)
    {
        NoteBatches = noteBatches;
        EventBatches = eventBatches;
        NoteCount = noteCount;
        EventCount = eventCount;
        NoteColor = ToSkColor(noteColor);
        EventColor = ToSkColor(eventColor);
    }

    public SKVertices[] NoteBatches { get; }

    public SKVertices[] EventBatches { get; }

    public int NoteCount { get; }

    public int EventCount { get; }

    public SKColor NoteColor { get; }

    public SKColor EventColor { get; }

    public int PrimitiveCount => NoteCount + EventCount;

    /// <summary>Reads the ARGB value of a brush so the batch key and the paint stay in sync.</summary>
    public static uint ColorOf(IBrush brush) =>
        brush is ISolidColorBrush solid ? solid.Color.ToUInt32() : 0xFFFFFFFFu;

    public static TimelineNoteVertexBatch Build(
        List<TimelineSegmentPreviewNote> notes,
        List<TimelineSegmentPreviewEvent> events,
        double width,
        double height,
        double devicePixel,
        uint noteColor,
        uint eventColor)
    {
        double safeWidth = Math.Max(1, width);
        double safeHeight = Math.Max(1, height);
        double rowHeight = Math.Max(devicePixel, safeHeight / 128d);
        SKVertices[] noteBatches = BuildNotes(notes, safeWidth, safeHeight, devicePixel, rowHeight);
        SKVertices[] eventBatches = BuildEvents(events, safeWidth, safeHeight, devicePixel);
        return new TimelineNoteVertexBatch(
            noteBatches,
            eventBatches,
            notes.Count,
            events.Count,
            noteColor,
            eventColor);
    }

    private static SKVertices[] BuildNotes(
        List<TimelineSegmentPreviewNote> notes,
        double width,
        double height,
        double devicePixel,
        double rowHeight)
    {
        if (notes.Count == 0)
        {
            return [];
        }

        var batches = new List<SKVertices>((notes.Count + QuadsPerBatch - 1) / QuadsPerBatch);
        int offset = 0;
        while (offset < notes.Count)
        {
            int count = Math.Min(QuadsPerBatch, notes.Count - offset);
            var positions = new SKPoint[count * 4];
            var indices = new ushort[count * 6];
            for (int index = 0; index < count; index++)
            {
                TimelineSegmentPreviewNote note = notes[offset + index];
                float left = (float)(Math.Clamp(note.NormalizedStart, 0, 1) * width);
                float right = (float)(Math.Clamp(note.NormalizedEnd, 0, 1) * width);
                float quadWidth = (float)Math.Max(devicePixel, right - left);
                float top = (float)((127 - Math.Clamp(note.Pitch, 0, 127)) / 128d * height);
                float bottom = (float)(top + rowHeight);
                WriteQuad(positions, indices, index, left, top, left + quadWidth, bottom);
            }

            batches.Add(SKVertices.CreateCopy(SKVertexMode.Triangles, positions, null, null, indices));
            offset += count;
        }

        return [.. batches];
    }

    private static SKVertices[] BuildEvents(
        List<TimelineSegmentPreviewEvent> events,
        double width,
        double height,
        double devicePixel)
    {
        if (events.Count == 0)
        {
            return [];
        }

        var batches = new List<SKVertices>((events.Count + QuadsPerBatch - 1) / QuadsPerBatch);
        int offset = 0;
        while (offset < events.Count)
        {
            int count = Math.Min(QuadsPerBatch, events.Count - offset);
            var positions = new SKPoint[count * 4];
            var indices = new ushort[count * 6];
            for (int index = 0; index < count; index++)
            {
                TimelineSegmentPreviewEvent value = events[offset + index];
                double raw = Math.Clamp(value.NormalizedTick, 0, 1) * width;
                float left = (float)(Math.Round(raw / devicePixel) * devicePixel);
                float top = (float)((1 - Math.Clamp(value.NormalizedValue, 0, 1)) * height);
                float bottom = (float)Math.Max(top + devicePixel, height);
                WriteQuad(
                    positions,
                    indices,
                    index,
                    left,
                    top,
                    left + (float)devicePixel,
                    bottom);
            }

            batches.Add(SKVertices.CreateCopy(SKVertexMode.Triangles, positions, null, null, indices));
            offset += count;
        }

        return [.. batches];
    }

    private static void WriteQuad(
        SKPoint[] positions,
        ushort[] indices,
        int index,
        float left,
        float top,
        float right,
        float bottom)
    {
        int vertex = index * 4;
        positions[vertex + 0] = new SKPoint(left, top);
        positions[vertex + 1] = new SKPoint(right, top);
        positions[vertex + 2] = new SKPoint(right, bottom);
        positions[vertex + 3] = new SKPoint(left, bottom);
        int target = index * 6;
        indices[target + 0] = (ushort)(vertex + 0);
        indices[target + 1] = (ushort)(vertex + 1);
        indices[target + 2] = (ushort)(vertex + 2);
        indices[target + 3] = (ushort)(vertex + 0);
        indices[target + 4] = (ushort)(vertex + 2);
        indices[target + 5] = (ushort)(vertex + 3);
    }

    private static SKColor ToSkColor(uint argb) => new(
        (byte)((argb >> 16) & 0xFF),
        (byte)((argb >> 8) & 0xFF),
        (byte)(argb & 0xFF),
        (byte)((argb >> 24) & 0xFF));
}

/// <summary>
/// Deterministic batch store with a hard entry ceiling. The ceiling keeps the memory budget
/// bounded without history dependent bookkeeping; when it is reached the store is emptied, which
/// only costs a rebuild on the next frame.
/// </summary>
internal sealed class TimelineNoteBatchCache
{
    public const int Capacity = 96;

    private readonly Dictionary<TimelineNoteBatchKey, TimelineNoteVertexBatch> _entries = [];

    public bool TryGet(in TimelineNoteBatchKey key, out TimelineNoteVertexBatch batch) =>
        _entries.TryGetValue(key, out batch!);

    public void Store(in TimelineNoteBatchKey key, TimelineNoteVertexBatch batch)
    {
        if (_entries.Count >= Capacity)
        {
            _entries.Clear();
        }

        _entries[key] = batch;
    }

    public void Clear() => _entries.Clear();
}

/// <summary>
/// P4 GPU path: one Segment preview (notes and events) is drawn as pre-built Skia vertex batches
/// by a single leased canvas. The operation is immutable, so an unchanged viewport compares equal
/// to the previous frame and the renderer reuses the recorded frame; panning only changes the
/// translation. Hit testing and every other timeline layer stay on the CPU drawing path.
/// </summary>
internal sealed class TimelineNoteDrawOperation : ICustomDrawOperation
{
    private readonly TimelineNoteVertexBatch _batch;
    private readonly Rect _clip;
    private readonly float _translateX;
    private readonly float _translateY;
    private readonly SKPaint _notePaint;
    private readonly SKPaint _eventPaint;

    public TimelineNoteDrawOperation(
        TimelineNoteVertexBatch batch,
        Rect clip,
        double translateX,
        double translateY)
    {
        _batch = batch;
        _clip = clip;
        _translateX = (float)translateX;
        _translateY = (float)translateY;
        _notePaint = new SKPaint { Color = batch.NoteColor, IsAntialias = false };
        _eventPaint = new SKPaint { Color = batch.EventColor, IsAntialias = false };
        Bounds = clip;
    }

    public Rect Bounds { get; }

    public void Dispose()
    {
        _notePaint.Dispose();
        _eventPaint.Dispose();
    }

    public bool Equals(ICustomDrawOperation? other) =>
        other is TimelineNoteDrawOperation operation
        && ReferenceEquals(operation._batch, _batch)
        && Math.Abs(operation._translateX - _translateX) < 0.01f
        && Math.Abs(operation._translateY - _translateY) < 0.01f
        && operation._clip.Equals(_clip);

    public bool HitTest(Point p) => false;

    public void Render(ImmediateDrawingContext context)
    {
        ISkiaSharpApiLeaseFeature? feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (feature is null)
        {
            return;
        }

        using ISkiaSharpApiLease lease = feature.Lease();
        SKCanvas canvas = lease.SkCanvas;
        int save = canvas.Save();
        canvas.ClipRect(new SKRect(
            (float)_clip.X,
            (float)_clip.Y,
            (float)_clip.Right,
            (float)_clip.Bottom));
        canvas.Translate(_translateX, _translateY);
        foreach (SKVertices batch in _batch.NoteBatches)
        {
            canvas.DrawVertices(batch, SKBlendMode.SrcOver, _notePaint);
        }

        foreach (SKVertices batch in _batch.EventBatches)
        {
            canvas.DrawVertices(batch, SKBlendMode.SrcOver, _eventPaint);
        }

        canvas.RestoreToCount(save);
    }
}
