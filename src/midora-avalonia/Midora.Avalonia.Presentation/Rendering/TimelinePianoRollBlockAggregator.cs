namespace Midora.Avalonia.Presentation.Rendering;

/// <summary>
/// Aggregates notes into fixed tick blocks per lane for the piano roll level of detail: every block
/// that receives a note becomes one segment spanning the block's first start to its maximum end.
/// Because the caller only merges while a block is a few pixels wide on screen, gaps inside a block
/// are sub-pixel, and a segment can never smear across a wide tick range the way note-count grouping
/// does. This is a struct sink so the traversal stays free of delegates and allocations; the backing
/// arrays and the touched list are reused across frames, and a stamp array avoids clearing them.
/// </summary>
public struct TimelinePianoRollBlockAggregator : TimelineLaneChunkIndex.IItemSink
{
    private long[] _minimumStart;
    private long[] _maximumEnd;
    private int[] _stamp;
    private List<int> _touched;
    private int _stampValue;
    private int _firstLane;
    private int _laneCount;
    private long _firstBlock;
    private int _blockCount;
    private int _blockTicks;

    public int LaneCount => _laneCount;

    public int BlockCount => _blockCount;

    public int BlockTicks => _blockTicks;

    /// <summary>Cells that received at least one note, in first-touch order.</summary>
    public IReadOnlyList<int> TouchedCells => _touched;

    public long MinimumStart(int cell) => _minimumStart[cell];

    public long MaximumEnd(int cell) => _maximumEnd[cell];

    public int LaneOf(int cell) => _firstLane + (cell / _blockCount);

    /// <summary>Starts a new aggregation pass, reusing the existing buffers.</summary>
    public void Begin(
        int firstLane,
        int laneCount,
        long firstBlock,
        int blockCount,
        int blockTicks)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(laneCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockTicks, 1);
        _firstLane = firstLane;
        _laneCount = laneCount;
        _firstBlock = firstBlock;
        _blockCount = blockCount;
        _blockTicks = blockTicks;
        // A struct stored as a field is default initialised without running any constructor, so every
        // reference field must be created here rather than in a parameterless constructor.
        _touched ??= [];
        _minimumStart ??= [];
        _maximumEnd ??= [];
        _stamp ??= [];
        _touched.Clear();
        int cells = checked(laneCount * blockCount);
        if (_minimumStart.Length < cells)
        {
            _minimumStart = new long[cells];
            _maximumEnd = new long[cells];
            _stamp = new int[cells];
            _stampValue = 0;
        }

        _stampValue++;
    }

    public void Add(in TimelineRenderItem item)
    {
        if (item.Kind is not (TimelineItemKind.DirectMidiNote or TimelineItemKind.LogicalNote))
        {
            return;
        }

        int laneIndex = item.Lane - _firstLane;
        long block = item.StartTick / _blockTicks - _firstBlock;
        if (laneIndex < 0 || laneIndex >= _laneCount || block < 0 || block >= _blockCount)
        {
            return;
        }

        int cell = (int)(laneIndex * (long)_blockCount + block);
        if (_stamp[cell] != _stampValue)
        {
            _stamp[cell] = _stampValue;
            _minimumStart[cell] = item.StartTick;
            _maximumEnd[cell] = item.EndTick;
            _touched.Add(cell);
            return;
        }

        if (item.StartTick < _minimumStart[cell])
        {
            _minimumStart[cell] = item.StartTick;
        }

        if (item.EndTick > _maximumEnd[cell])
        {
            _maximumEnd[cell] = item.EndTick;
        }
    }
}
