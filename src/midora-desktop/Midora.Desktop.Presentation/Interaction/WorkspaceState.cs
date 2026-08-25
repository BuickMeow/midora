using Midora.Domain;

namespace Midora.Desktop.Presentation.Interaction;

public enum WorkspaceKind
{
    Arrangement,
    EventInstrumentLibrary,
    ProjectSettings,
    Diagnostics,
    ConductorTrack,
    SegmentEditor,
    EventInstrumentEditor
}

public readonly record struct WorkspaceKey(WorkspaceKind Kind, MidoraId? ObjectId)
{
    public static WorkspaceKey ForType(WorkspaceKind kind)
    {
        if (kind is WorkspaceKind.SegmentEditor
            or WorkspaceKind.EventInstrumentEditor)
        {
            throw new ArgumentException("This workspace kind requires an object identity.", nameof(kind));
        }
        return new(kind, null);
    }

    public static WorkspaceKey ForObject(WorkspaceKind kind, MidoraId objectId)
    {
        if (kind is not (WorkspaceKind.SegmentEditor
            or WorkspaceKind.EventInstrumentEditor))
        {
            throw new ArgumentException("This workspace kind is unique by type.", nameof(kind));
        }
        if (objectId.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(objectId));
        }
        return new(kind, objectId);
    }
}

public enum WorkspaceSelectionRangeMode
{
    Replace,
    Add,
    Toggle,
    Remove
}

public sealed class WorkspaceSelection
{
    private readonly HashSet<MidoraId> _ids = [];

    public IReadOnlyCollection<MidoraId> Ids => _ids;
    public MidoraId? Primary { get; private set; }
    public MidoraId? Anchor { get; private set; }
    public long Revision { get; private set; }

    public void Replace(MidoraId id)
    {
        Validate(id);
        if (_ids.Count == 1 && _ids.Contains(id) && Primary == id && Anchor == id)
        {
            return;
        }
        _ids.Clear();
        _ids.Add(id);
        Primary = id;
        Anchor = id;
        Revision = checked(Revision + 1);
    }

    public void Add(MidoraId id, bool makePrimary = true)
    {
        Validate(id);
        bool changed = _ids.Add(id);
        if (makePrimary || Primary is null)
        {
            changed |= Primary != id;
            Primary = id;
        }
        if (Anchor is null)
        {
            Anchor = id;
            changed = true;
        }
        if (changed) Revision = checked(Revision + 1);
    }

    public void Toggle(MidoraId id)
    {
        Validate(id);
        if (!_ids.Remove(id))
        {
            Add(id);
            return;
        }
        if (Primary == id)
        {
            Primary = _ids.Count == 0 ? null : _ids.Min();
        }
        if (Anchor == id)
        {
            Anchor = Primary;
        }
        Revision = checked(Revision + 1);
    }

    public void Remove(MidoraId id)
    {
        if (!_ids.Remove(id))
        {
            return;
        }
        if (Primary == id)
        {
            Primary = _ids.Count == 0 ? null : _ids.Min();
        }
        if (Anchor == id)
        {
            Anchor = Primary;
        }
        Revision = checked(Revision + 1);
    }

    public void Clear()
    {
        if (_ids.Count == 0 && Primary is null && Anchor is null)
        {
            return;
        }
        _ids.Clear();
        Primary = null;
        Anchor = null;
        Revision = checked(Revision + 1);
    }

    public void ApplyRange(
        IEnumerable<MidoraId> ids,
        WorkspaceSelectionRangeMode mode)
    {
        ArgumentNullException.ThrowIfNull(ids);
        MidoraId[] materialized = ids.Distinct().ToArray();
        foreach (MidoraId id in materialized) Validate(id);
        HashSet<MidoraId> before = new(_ids);
        MidoraId? beforePrimary = Primary;
        MidoraId? beforeAnchor = Anchor;
        switch (mode)
        {
            case WorkspaceSelectionRangeMode.Replace:
                _ids.Clear();
                _ids.UnionWith(materialized);
                Primary = materialized.Length == 0 ? null : materialized[0];
                Anchor = Primary;
                break;
            case WorkspaceSelectionRangeMode.Add:
                _ids.UnionWith(materialized);
                if (Primary is null && materialized.Length != 0)
                {
                    Primary = materialized[0];
                    Anchor ??= Primary;
                }
                break;
            case WorkspaceSelectionRangeMode.Toggle:
                foreach (MidoraId id in materialized)
                {
                    if (!_ids.Remove(id)) _ids.Add(id);
                }
                NormalizeEndpoints();
                break;
            case WorkspaceSelectionRangeMode.Remove:
                _ids.ExceptWith(materialized);
                NormalizeEndpoints();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (!before.SetEquals(_ids) || beforePrimary != Primary || beforeAnchor != Anchor)
        {
            Revision = checked(Revision + 1);
        }

        void NormalizeEndpoints()
        {
            if (Primary is MidoraId primary && !_ids.Contains(primary))
            {
                Primary = _ids.Count == 0 ? null : _ids.Min();
            }
            if (Anchor is MidoraId anchor && !_ids.Contains(anchor))
            {
                Anchor = Primary;
            }
            if (_ids.Count == 0)
            {
                Primary = null;
                Anchor = null;
            }
        }
    }

    private static void Validate(MidoraId id)
    {
        if (id.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id));
        }
    }
}

public static class TimelineSnap
{
    public static long Snap(long tick, long gridStep, int movementDirection)
    {
        if (tick < 0 || gridStep <= 0 || movementDirection is < -1 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(tick));
        }

        long lower = tick / gridStep * gridStep;
        long upper = lower == tick ? lower : checked(lower + gridStep);
        long lowerDistance = tick - lower;
        long upperDistance = upper - tick;
        if (lowerDistance < upperDistance)
        {
            return lower;
        }
        if (upperDistance < lowerDistance)
        {
            return upper;
        }
        return movementDirection > 0 ? upper : lower;
    }
}

public static class TimelineGridQuantization
{
    public readonly record struct SnappedRange(long StartTick, long EndTick);

    public static long SnapAbsolute(
        long tick,
        long fixedStepTicks,
        bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap,
        int movementDirection)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        if (!useBars || timeSignatureMap is null)
        {
            return TimelineSnap.Snap(tick, Math.Max(1, fixedStepTicks), movementDirection);
        }

        ProjectBarInfo bar = timeSignatureMap.GetBarContaining(tick);
        long lowerDistance = tick - bar.StartTick;
        long upperDistance = bar.EndTick - tick;
        if (lowerDistance < upperDistance)
        {
            return bar.StartTick;
        }
        if (upperDistance < lowerDistance)
        {
            return bar.EndTick;
        }
        return movementDirection > 0 ? bar.EndTick : bar.StartTick;
    }

    public static long SnapDelta(
        long delta,
        long targetTick,
        long fixedStepTicks,
        bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap)
    {
        if (delta == 0)
        {
            return 0;
        }

        long step = Math.Max(1, fixedStepTicks);
        if (useBars && timeSignatureMap is not null)
        {
            ProjectBarInfo bar = timeSignatureMap.GetBarContaining(Math.Max(0, targetTick));
            step = Math.Max(1, checked(bar.EndTick - bar.StartTick));
        }

        long magnitude = delta == long.MinValue ? long.MaxValue : Math.Abs(delta);
        long snapped = TimelineSnap.Snap(magnitude, step, Math.Sign(delta));
        return delta < 0 ? -snapped : snapped;
    }

    public static long GetGridTickAtOrAfter(
        long tick,
        long fixedStepTicks,
        bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        if (useBars && timeSignatureMap is not null)
        {
            ProjectBarInfo bar = timeSignatureMap.GetBarContaining(tick);
            return bar.StartTick == tick ? tick : bar.EndTick;
        }

        long step = Math.Max(1, fixedStepTicks);
        long lower = tick / step * step;
        return lower == tick ? tick : checked(lower + step);
    }

    public static long GetNextGridTick(
        long tick,
        long fixedStepTicks,
        bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        if (useBars && timeSignatureMap is not null)
        {
            ProjectBarInfo bar = timeSignatureMap.GetBarContaining(tick);
            if (bar.EndTick > tick)
            {
                return bar.EndTick;
            }

            return timeSignatureMap.GetBarContaining(checked(tick + 1)).EndTick;
        }

        return checked(tick + Math.Max(1, fixedStepTicks));
    }

    public static long GetPreviousGridTick(
        long tick,
        long fixedStepTicks,
        bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        if (tick == 0)
        {
            return 0;
        }
        if (useBars && timeSignatureMap is not null)
        {
            return timeSignatureMap.GetBarContaining(tick - 1).StartTick;
        }

        return Math.Max(0, tick - Math.Max(1, fixedStepTicks));
    }

    public static SnappedRange SnapRangeFromAnchor(
        long rawAnchorTick,
        long rawMovingTick,
        long fixedStepTicks,
        bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rawAnchorTick);
        ArgumentOutOfRangeException.ThrowIfNegative(rawMovingTick);
        bool movesRight = rawMovingTick >= rawAnchorTick;
        long anchor = SnapAbsolute(
            rawAnchorTick,
            fixedStepTicks,
            useBars,
            timeSignatureMap,
            0);
        long moving = SnapAbsolute(
            rawMovingTick,
            fixedStepTicks,
            useBars,
            timeSignatureMap,
            movesRight ? 1 : -1);

        if (movesRight)
        {
            long end = moving > anchor
                ? moving
                : GetNextGridTick(anchor, fixedStepTicks, useBars, timeSignatureMap);
            return new(anchor, end);
        }

        long start = moving < anchor
            ? moving
            : GetPreviousGridTick(anchor, fixedStepTicks, useBars, timeSignatureMap);
        if (start < anchor)
        {
            return new(start, anchor);
        }

        long fallbackEnd = GetNextGridTick(anchor, fixedStepTicks, useBars, timeSignatureMap);
        return new(anchor, fallbackEnd);
    }

    public static SnappedRange SnapPositiveRange(
        long rawStartTick,
        long rawEndTick,
        long fixedStepTicks,
        bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rawStartTick);
        if (rawEndTick <= rawStartTick)
        {
            throw new ArgumentOutOfRangeException(nameof(rawEndTick));
        }
        return SnapRangeFromAnchor(
            rawStartTick,
            rawEndTick,
            fixedStepTicks,
            useBars,
            timeSignatureMap);
    }
}
