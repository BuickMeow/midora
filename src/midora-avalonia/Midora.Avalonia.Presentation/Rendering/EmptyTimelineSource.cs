using Midora.Domain;

namespace Midora.Avalonia.Presentation.Rendering;

/// <summary>
/// Zero-item source for an empty (new) project: correct Count/extent/fingerprint without the
/// demo content.
/// </summary>
public sealed class EmptyTimelineSource : ITimelineRenderItemSource, INonBlockingTimelineFingerprintSource
{
    public static EmptyTimelineSource Instance { get; } = new();

    private EmptyTimelineSource()
    {
    }

    public long Count => 0;

    public long MaximumEndTick => 1;

    public ulong ContentFingerprint => 0;

    public bool CanComputeRangeFingerprintWithoutBlocking => true;

    public bool HasHitTestableItems => false;

    public ulong GetRangeFingerprint(long startTick, long endTick, int firstLane, int lastLane) => 0;

    public void QueryInto(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        List<TimelineRenderItem> destination)
    {
    }

    public void VisitInto(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        Action<TimelineRenderItem> visitor)
    {
    }

    public bool TryGetById(MidoraId id, out TimelineRenderItem item)
    {
        item = default;
        return false;
    }

    public IEnumerable<TimelineRenderItem> EnumerateAll()
    {
        return [];
    }
}
