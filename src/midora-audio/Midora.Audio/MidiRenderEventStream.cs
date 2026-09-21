using Midora.Midi;

namespace Midora.Audio;

public readonly record struct ScheduledPortMidiMessage
{
    public ScheduledPortMidiMessage(
        byte zeroBasedPortNumber,
        ScheduledMidiMessage scheduled)
    {
        if (zeroBasedPortNumber >= 16)
            throw new ArgumentOutOfRangeException(nameof(zeroBasedPortNumber));
        ZeroBasedPortNumber = zeroBasedPortNumber;
        Scheduled = scheduled;
    }

    public byte ZeroBasedPortNumber { get; }
    public ScheduledMidiMessage Scheduled { get; }
}

public interface IMidiRenderEventPageProvider
{
    IEnumerable<ScheduledPortMidiMessage> Query(
        long startFrame,
        long endFrame,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional provider contract used by the rolling playback stream. Providers
/// implementing this interface can avoid touching source pages whose audio is
/// already supplied by an exact PCM-cache generation.
/// </summary>
public interface IMidiRenderEventDemandAwarePageProvider : IMidiRenderEventPageProvider
{
    IEnumerable<ScheduledPortMidiMessage> Query(
        long startFrame,
        long endFrame,
        MidiRenderEventDemandSnapshot demand,
        CancellationToken cancellationToken = default);
}

public sealed class MidiRenderEventDemandSnapshot
{
    private readonly bool[] _sourceEnabled;
    private readonly bool[] _cacheOwnerBypassed;
    private readonly int[][] _cacheOwnersBySource;
    private readonly MidiRenderSourceCacheInterval[][] _cacheIntervalsByOwner;

    internal MidiRenderEventDemandSnapshot(
        bool[] sourceEnabled,
        bool[] cacheOwnerBypassed,
        int[][] cacheOwnersBySource,
        MidiRenderSourceCacheInterval[][] cacheIntervalsByOwner)
    {
        _sourceEnabled = sourceEnabled;
        _cacheOwnerBypassed = cacheOwnerBypassed;
        _cacheOwnersBySource = cacheOwnersBySource;
        _cacheIntervalsByOwner = cacheIntervalsByOwner;
    }

    public int SourceCount => _sourceEnabled.Length;

    public bool IsSourceDemanded(int sourceIndex, long sampleFrame)
    {
        if ((uint)sourceIndex >= (uint)_sourceEnabled.Length || sampleFrame < 0)
        {
            return false;
        }
        if (!_sourceEnabled[sourceIndex])
        {
            return false;
        }

        int[] owners = _cacheOwnersBySource[sourceIndex];
        if (owners.Length == 0)
        {
            return true;
        }
        foreach (int owner in owners)
        {
            if (_cacheOwnerBypassed[owner]
                || OwnerRequiresSynthesis(owner, sampleFrame, sampleFrame))
            {
                return true;
            }
        }
        return false;
    }

    public bool MayDemandSource(int sourceIndex, long startFrame, long endFrame)
    {
        if ((uint)sourceIndex >= (uint)_sourceEnabled.Length
            || startFrame < 0
            || endFrame <= startFrame
            || !_sourceEnabled[sourceIndex])
        {
            return false;
        }

        int[] owners = _cacheOwnersBySource[sourceIndex];
        if (owners.Length == 0)
        {
            return true;
        }
        foreach (int owner in owners)
        {
            if (_cacheOwnerBypassed[owner]
                || OwnerRequiresSynthesis(owner, startFrame, endFrame - 1))
            {
                return true;
            }
        }
        return false;
    }

    public int CountDemandedSources(long startFrame, long endFrame)
    {
        int count = 0;
        for (int sourceIndex = 0; sourceIndex < _sourceEnabled.Length; sourceIndex++)
        {
            if (MayDemandSource(sourceIndex, startFrame, endFrame))
            {
                count++;
            }
        }
        return count;
    }

    private bool OwnerRequiresSynthesis(int owner, long firstFrame, long lastFrame)
    {
        MidiRenderSourceCacheInterval[] intervals = _cacheIntervalsByOwner[owner];
        if (intervals.Length == 0)
        {
            return true;
        }

        int low = 0;
        int high = intervals.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (intervals[middle].EndFrame <= firstFrame)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        for (int index = low;
            index < intervals.Length && intervals[index].StartFrame <= lastFrame;
            index++)
        {
            if (!intervals[index].IsExactPcmHit)
            {
                return true;
            }
        }
        return false;
    }
}

internal readonly record struct MidiRenderSourceCacheInterval(
    long StartFrame,
    long EndFrame,
    bool IsExactPcmHit);

internal sealed class MidiRenderEventDemandState
{
    private readonly object _sync = new();
    private readonly bool[] _sourceEnabled;
    private readonly bool[] _cacheOwnerBypassed;
    private readonly int[][] _cacheOwnersBySource;
    private readonly MidiRenderSourceCacheInterval[][] _cacheIntervalsByOwner;

    public MidiRenderEventDemandState(MidiRenderPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        int sourceCount = plan.SourceIds.Length;
        _sourceEnabled = new bool[sourceCount];
        Array.Fill(_sourceEnabled, true);
        _cacheOwnerBypassed = new bool[sourceCount];
        _cacheOwnersBySource = CreateCacheOwnerMap(plan, sourceCount);
        _cacheIntervalsByOwner = CreateIntervals(plan, sourceCount);
        foreach (int sourceIndex in plan.InitiallyDisabledSourceIndices)
        {
            _sourceEnabled[sourceIndex] = false;
            foreach (int owner in _cacheOwnersBySource[sourceIndex])
            {
                _cacheOwnerBypassed[owner] = true;
            }
        }
    }

    public MidiRenderEventDemandSnapshot Capture()
    {
        lock (_sync)
        {
            return new(
                (bool[])_sourceEnabled.Clone(),
                (bool[])_cacheOwnerBypassed.Clone(),
                _cacheOwnersBySource,
                _cacheIntervalsByOwner);
        }
    }

    public bool ApplyMonitoringCommands(ReadOnlySpan<MidiMonitoringCommand> commands)
    {
        bool changed = false;
        lock (_sync)
        {
            foreach (MidiMonitoringCommand command in commands)
            {
                if (command.Kind != MidiMonitoringCommandKind.SetSourceEnabled)
                {
                    continue;
                }
                int sourceIndex = command.SourceIndex;
                if ((uint)sourceIndex >= (uint)_sourceEnabled.Length)
                {
                    throw new ArgumentException(
                        "A monitoring command references an invalid event-demand source.",
                        nameof(commands));
                }
                if (_sourceEnabled[sourceIndex] == command.SourceEnabled)
                {
                    continue;
                }
                changed = true;
                _sourceEnabled[sourceIndex] = command.SourceEnabled;
                foreach (int owner in _cacheOwnersBySource[sourceIndex])
                {
                    // The renderer follows the same monotonic rule: once live
                    // monitoring changes a cached stem, that owner remains on
                    // synthesis for the rest of this playback generation.
                    _cacheOwnerBypassed[owner] = true;
                }
            }
        }
        return changed;
    }

    private static int[][] CreateCacheOwnerMap(MidiRenderPlan plan, int sourceCount)
    {
        MidiUnitFragmentRenderPlan[] fragments = plan.UnitFragments.ToArray();
        MidiRenderCacheSourceBinding[] bindings = plan.CacheSourceBindings.ToArray();
        int[][] result = new int[sourceCount][];
        for (int sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)
        {
            HashSet<int> owners = [];
            foreach (MidiRenderCacheSourceBinding binding in bindings)
            {
                if (binding.SourceIndex == sourceIndex)
                {
                    owners.Add(binding.CacheOwnerSourceIndex);
                }
            }
            foreach (MidiUnitFragmentRenderPlan fragment in fragments)
            {
                if (fragment.SourceIndex == sourceIndex)
                {
                    owners.Add(sourceIndex);
                    continue;
                }
                foreach (ScheduledMidiMessage scheduled in fragment.Events)
                {
                    if (scheduled.SourceIndex == sourceIndex)
                    {
                        owners.Add(fragment.SourceIndex);
                        break;
                    }
                }
            }
            result[sourceIndex] = owners.Order().ToArray();
        }
        return result;
    }

    private static MidiRenderSourceCacheInterval[][] CreateIntervals(
        MidiRenderPlan plan,
        int sourceCount)
    {
        MidiRenderSourceCacheInterval[][] result =
            new MidiRenderSourceCacheInterval[sourceCount][];
        MidiSegmentRenderPlan[] segments = plan.Segments.ToArray();
        for (int sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)
        {
            result[sourceIndex] = segments
                .Where(value => value.SourceIndex == sourceIndex)
                .OrderBy(value => value.StartFrame)
                .ThenBy(value => value.SegmentId)
                .Select(value => new MidiRenderSourceCacheInterval(
                    value.StartFrame,
                    value.EndFrame,
                    value.PcmCacheKey is not null && value.PcmCacheHit))
                .ToArray();
        }
        return result;
    }
}

public sealed record MidiRenderEventStreamDescriptor
{
    public MidiRenderEventStreamDescriptor(string controlFilePath, string dataFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFilePath);
        ControlFilePath = controlFilePath;
        DataFilePath = dataFilePath;
    }

    public string ControlFilePath { get; }
    public string DataFilePath { get; }
}

public readonly record struct MidiRenderUnitDescriptor
{
    public MidiRenderUnitDescriptor(byte zeroBasedPortNumber, byte zeroBasedChannelNumber)
    {
        if (zeroBasedPortNumber >= 16)
            throw new ArgumentOutOfRangeException(nameof(zeroBasedPortNumber));
        if (zeroBasedChannelNumber >= 16)
            throw new ArgumentOutOfRangeException(nameof(zeroBasedChannelNumber));
        ZeroBasedPortNumber = zeroBasedPortNumber;
        ZeroBasedChannelNumber = zeroBasedChannelNumber;
    }

    public byte ZeroBasedPortNumber { get; }
    public byte ZeroBasedChannelNumber { get; }
    public int CanonicalUnitNumber => ZeroBasedPortNumber * 16 + ZeroBasedChannelNumber;
}

public readonly record struct MidiRenderCacheSourceBinding
{
    public MidiRenderCacheSourceBinding(int sourceIndex, int cacheOwnerSourceIndex)
    {
        if (sourceIndex < 0) throw new ArgumentOutOfRangeException(nameof(sourceIndex));
        if (cacheOwnerSourceIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(cacheOwnerSourceIndex));
        SourceIndex = sourceIndex;
        CacheOwnerSourceIndex = cacheOwnerSourceIndex;
    }

    public int SourceIndex { get; }
    public int CacheOwnerSourceIndex { get; }
}
