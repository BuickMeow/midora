using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Midora.Avalonia.Presentation.Rendering;
using SkiaSharp;

namespace Midora.Avalonia.Presentation.Controls;

/// <summary>
/// Accumulates axis-aligned quads into immutable Skia vertex batches. Every batch stays below the
/// 16-bit index limit, and the builder is reusable so a frame does not allocate per primitive.
/// </summary>
internal sealed class TimelineQuadBatchBuilder
{
    private SKPoint[] _positions = new SKPoint[TimelineNoteVertexBatch.QuadsPerBatch * 4];
    private ushort[] _indices = new ushort[TimelineNoteVertexBatch.QuadsPerBatch * 6];
    private readonly List<SKVertices> _batches = [];
    private int _count;

    public int Count => _batches.Count * TimelineNoteVertexBatch.QuadsPerBatch + _count;

    public void Add(float left, float top, float right, float bottom)
    {
        if (_count == TimelineNoteVertexBatch.QuadsPerBatch)
        {
            Flush();
        }

        int vertex = _count * 4;
        _positions[vertex + 0] = new SKPoint(left, top);
        _positions[vertex + 1] = new SKPoint(right, top);
        _positions[vertex + 2] = new SKPoint(right, bottom);
        _positions[vertex + 3] = new SKPoint(left, bottom);
        int target = _count * 6;
        _indices[target + 0] = (ushort)(vertex + 0);
        _indices[target + 1] = (ushort)(vertex + 1);
        _indices[target + 2] = (ushort)(vertex + 2);
        _indices[target + 3] = (ushort)(vertex + 0);
        _indices[target + 4] = (ushort)(vertex + 2);
        _indices[target + 5] = (ushort)(vertex + 3);
        _count++;
    }

    /// <summary>Finishes the batch list and resets the builder for the next frame.</summary>
    public (SKVertices[] Batches, int Count) Complete()
    {
        int total = _batches.Count * TimelineNoteVertexBatch.QuadsPerBatch + _count;
        Flush();
        SKVertices[] batches = [.. _batches];
        _batches.Clear();
        _count = 0;
        return (batches, total);
    }

    private void Flush()
    {
        if (_count == 0)
        {
            return;
        }

        _batches.Add(SKVertices.CreateCopy(
            SKVertexMode.Triangles,
            _positions[..(_count * 4)],
            null,
            null,
            _indices[..(_count * 6)]));
        _count = 0;
    }
}

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
    uint EventColor,
    double VisibleStart,
    double VisibleEnd);

/// <summary>
/// Immutable Skia vertex batches for one Segment preview. Vertices are stored in pixels relative
/// to the Segment's full rectangle origin, so horizontal panning only changes the drawing
/// translation and never rebuilds the data; zoom, lane height, device pixel or content changes
/// produce a new batch. Each batch stays below the 16-bit index limit (16k quads = 64k vertices).
/// </summary>
internal sealed class TimelineNoteVertexBatch : IDisposable
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
        NotePaint = new SKPaint { Color = NoteColor, IsAntialias = false };
        EventPaint = new SKPaint { Color = EventColor, IsAntialias = false };
    }

    public SKVertices[] NoteBatches { get; }

    public SKVertices[] EventBatches { get; }

    public int NoteCount { get; }

    public int EventCount { get; }

    public SKColor NoteColor { get; }

    public SKColor EventColor { get; }

    /// <summary>Paints owned by the batch so a frame does not allocate one per Segment.</summary>
    public SKPaint NotePaint { get; }

    public SKPaint EventPaint { get; }

    public int PrimitiveCount => NoteCount + EventCount;

    /// <summary>Approximate retained size of the vertex and index data, used by the review trace.</summary>
    public long VertexBytes => (long)PrimitiveCount * 44;

    /// <summary>
    /// Releases the native Skia vertex buffers. Called by the cache when a batch is evicted; a batch
    /// handed to a draw operation stays alive until then.
    /// </summary>
    public void Dispose()
    {
        foreach (SKVertices batch in NoteBatches)
        {
            batch.Dispose();
        }

        foreach (SKVertices batch in EventBatches)
        {
            batch.Dispose();
        }

        NotePaint.Dispose();
        EventPaint.Dispose();
    }

    /// <summary>Reads the ARGB value of a brush so the batch key and the paint stay in sync.</summary>
    public static uint ColorOf(IBrush brush) =>
        brush is ISolidColorBrush solid ? solid.Color.ToUInt32() : 0xFFFFFFFFu;

    /// <summary>Creates a batch from already built quads, used by the piano roll surface.</summary>
    public static TimelineNoteVertexBatch FromQuads(
        SKVertices[] batches,
        int quadCount,
        uint color) =>
        new(batches, [], quadCount, 0, color, color);

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
internal sealed class TimelineNoteBatchCache : TimelineVertexBatchCache<TimelineNoteBatchKey>
{
}

/// <summary>
/// Deterministic batch store with a hard byte ceiling and least-recently-used eviction. Evicted
/// batches release their native Skia buffers, so the store cannot leak across rebuilds.
/// </summary>
internal class TimelineVertexBatchCache<TKey>
    where TKey : notnull
{
    /// <summary>Hard ceiling for retained vertex data; eviction keeps the process bounded.</summary>
    public const long BudgetBytes = 256L * 1024 * 1024;

    private readonly Dictionary<TKey, CacheEntry> _entries = [];
    private long _clock;

    public int Count => _entries.Count;

    public long Hits { get; private set; }

    public long Misses { get; private set; }

    public long TotalVertexBytes
    {
        get
        {
            long total = 0;
            foreach (CacheEntry entry in _entries.Values)
            {
                total += entry.Batch.VertexBytes;
            }

            return total;
        }
    }

    public bool TryGet(in TKey key, out TimelineNoteVertexBatch batch)
    {
        if (_entries.TryGetValue(key, out CacheEntry entry))
        {
            Hits++;
            entry.LastUsed = ++_clock;
            _entries[key] = entry;
            batch = entry.Batch;
            return true;
        }

        batch = null!;
        return false;
    }

    public void Store(in TKey key, TimelineNoteVertexBatch batch)
    {
        Misses++;
        _entries[key] = new CacheEntry(batch, ++_clock);
        EvictOverBudget();
    }

    public void Clear()
    {
        foreach (CacheEntry entry in _entries.Values)
        {
            entry.Batch.Dispose();
        }

        _entries.Clear();
    }

    /// <summary>
    /// Drops least recently used batches until the retained vertex bytes fit the budget. Evicted
    /// batches release their native Skia buffers, so the cache cannot leak across rebuilds.
    /// </summary>
    private void EvictOverBudget()
    {
        while (_entries.Count > 1 && TotalVertexBytes > BudgetBytes)
        {
            TKey? oldestKey = default;
            bool found = false;
            long oldestUse = long.MaxValue;
            foreach (KeyValuePair<TKey, CacheEntry> entry in _entries)
            {
                if (entry.Value.LastUsed < oldestUse)
                {
                    oldestUse = entry.Value.LastUsed;
                    oldestKey = entry.Key;
                    found = true;
                }
            }

            if (!found || oldestKey is null || !_entries.Remove(oldestKey, out CacheEntry evicted))
            {
                return;
            }

            evicted.Batch.Dispose();
        }
    }

    private struct CacheEntry
    {
        public CacheEntry(TimelineNoteVertexBatch batch, long lastUsed)
        {
            Batch = batch;
            LastUsed = lastUsed;
        }

        public TimelineNoteVertexBatch Batch { get; }

        public long LastUsed { get; set; }
    }
}

/// <summary>
/// One entry of the GPU preview pass: a cached batch plus the clip and translation that place it
/// for the current frame.
/// </summary>
internal readonly record struct TimelineNoteDrawEntry(
    TimelineNoteVertexBatch Batch,
    Rect Clip,
    float TranslateX,
    float TranslateY);

/// <summary>
/// P4 GPU path: every visible Segment preview (notes and events) is drawn as pre-built Skia vertex
/// batches by a single leased canvas, so a frame acquires one lease instead of one per Segment. The
/// operation snapshots the immutable entry list, so an unchanged viewport compares equal to the
/// previous frame and the renderer reuses the recorded frame; panning only changes translations.
/// Hit testing and every other timeline layer stay on the CPU drawing path.
/// </summary>
internal sealed class TimelineNoteDrawOperation : ICustomDrawOperation
{
    private readonly TimelineNoteDrawEntry[] _entries;

    public TimelineNoteDrawOperation(IReadOnlyList<TimelineNoteDrawEntry> entries, Rect bounds)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = [.. entries];
        Bounds = bounds;
    }

    public Rect Bounds { get; }

    public void Dispose()
    {
    }

    public bool Equals(ICustomDrawOperation? other)
    {
        if (other is not TimelineNoteDrawOperation operation
            || operation._entries.Length != _entries.Length)
        {
            return false;
        }

        for (int index = 0; index < _entries.Length; index++)
        {
            TimelineNoteDrawEntry left = _entries[index];
            TimelineNoteDrawEntry right = operation._entries[index];
            if (!ReferenceEquals(left.Batch, right.Batch)
                || !left.Clip.Equals(right.Clip)
                || Math.Abs(left.TranslateX - right.TranslateX) >= 0.01f
                || Math.Abs(left.TranslateY - right.TranslateY) >= 0.01f)
            {
                return false;
            }
        }

        return true;
    }

    public bool HitTest(Point p) => false;

    public void Render(ImmediateDrawingContext context)
    {
        if (_entries.Length == 0)
        {
            return;
        }

        ISkiaSharpApiLeaseFeature? feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (feature is null)
        {
            return;
        }

        using ISkiaSharpApiLease lease = feature.Lease();
        SKCanvas canvas = lease.SkCanvas;
        foreach (TimelineNoteDrawEntry entry in _entries)
        {
            int save = canvas.Save();
            canvas.ClipRect(new SKRect(
                (float)entry.Clip.X,
                (float)entry.Clip.Y,
                (float)entry.Clip.Right,
                (float)entry.Clip.Bottom));
            canvas.Translate(entry.TranslateX, entry.TranslateY);
            foreach (SKVertices batch in entry.Batch.NoteBatches)
            {
                canvas.DrawVertices(batch, SKBlendMode.SrcOver, entry.Batch.NotePaint);
            }

            foreach (SKVertices batch in entry.Batch.EventBatches)
            {
                canvas.DrawVertices(batch, SKBlendMode.SrcOver, entry.Batch.EventPaint);
            }

            canvas.RestoreToCount(save);
        }
    }
}
