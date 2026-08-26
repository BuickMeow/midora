using System.Collections.Immutable;
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
    private ImmutableHashSet<MidoraId> _ids = ImmutableHashSet<MidoraId>.Empty;

    public IReadOnlyCollection<MidoraId> Ids => _ids;
    public IReadOnlySet<MidoraId> IdSet => _ids;
    internal ImmutableHashSet<MidoraId> SharedIds => _ids;
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
        _ids = ImmutableHashSet.Create(id);
        Primary = id;
        Anchor = id;
        Revision = checked(Revision + 1);
    }

    public void Add(MidoraId id, bool makePrimary = true)
    {
        Validate(id);
        ImmutableHashSet<MidoraId> replacement = _ids.Add(id);
        bool changed = !ReferenceEquals(replacement, _ids);
        _ids = replacement;
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
        if (!_ids.Contains(id))
        {
            Add(id);
            return;
        }
        _ids = _ids.Remove(id);
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
        ImmutableHashSet<MidoraId> replacement = _ids.Remove(id);
        if (ReferenceEquals(replacement, _ids))
        {
            return;
        }
        _ids = replacement;
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

    /// <summary>
    /// Atomically removes every selected ID not present in <paramref name="validIds"/>.
    /// Large post-edit selection pruning must publish one immutable root and one
    /// revision instead of allocating a new tree once per deleted object.
    /// </summary>
    public bool RetainOnly(IReadOnlySet<MidoraId> validIds)
    {
        ArgumentNullException.ThrowIfNull(validIds);
        ImmutableHashSet<MidoraId> replacement = _ids.Intersect(validIds);
        if (replacement.Count == _ids.Count) return false;

        _ids = replacement;
        if (Primary is MidoraId primary && !_ids.Contains(primary))
            Primary = _ids.Count == 0 ? null : _ids.Min();
        if (Anchor is MidoraId anchor && !_ids.Contains(anchor)) Anchor = Primary;
        Revision = checked(Revision + 1);
        return true;
    }

    public void Clear()
    {
        if (_ids.Count == 0 && Primary is null && Anchor is null)
        {
            return;
        }
        _ids = ImmutableHashSet<MidoraId>.Empty;
        Primary = null;
        Anchor = null;
        Revision = checked(Revision + 1);
    }

    /// <summary>
    /// Atomically adopts a fully materialized immutable selection. This allows
    /// an exact, potentially very large selection to be built away from the UI
    /// thread and installed without copying it again on the Dispatcher thread.
    /// </summary>
    public void AdoptMaterialized(
        ImmutableHashSet<MidoraId> ids,
        MidoraId? primary = null,
        MidoraId? anchor = null)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (primary is MidoraId primaryId && !ids.Contains(primaryId))
        {
            throw new ArgumentException(
                "Primary selection must belong to the selection set.",
                nameof(primary));
        }
        if (anchor is MidoraId anchorId && !ids.Contains(anchorId))
        {
            throw new ArgumentException(
                "Selection anchor must belong to the selection set.",
                nameof(anchor));
        }
        _ids = ids;
        Primary = primary;
        Anchor = anchor ?? primary;
        Revision = checked(Revision + 1);
    }

    public bool ReplaceAll(IEnumerable<MidoraId> ids, MidoraId? primary)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ImmutableHashSet<MidoraId>.Builder builder = ImmutableHashSet.CreateBuilder<MidoraId>();
        MidoraId? first = null;
        foreach (MidoraId id in ids)
        {
            Validate(id);
            if (builder.Add(id)) first ??= id;
        }
        ImmutableHashSet<MidoraId> replacement = builder.ToImmutable();
        if (primary is MidoraId primaryId && !replacement.Contains(primaryId))
        {
            throw new ArgumentException(
                "Primary selection must belong to the replacement set.",
                nameof(primary));
        }
        MidoraId? normalizedPrimary = primary
            ?? first;
        if (Primary == normalizedPrimary && _ids.SetEquals(replacement))
        {
            return false;
        }
        _ids = replacement;
        Primary = normalizedPrimary;
        Anchor = normalizedPrimary;
        Revision = checked(Revision + 1);
        return true;
    }

    public void ApplyRange(
        IEnumerable<MidoraId> ids,
        WorkspaceSelectionRangeMode mode)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ImmutableHashSet<MidoraId>.Builder builder = ImmutableHashSet.CreateBuilder<MidoraId>();
        MidoraId? first = null;
        foreach (MidoraId id in ids)
        {
            Validate(id);
            if (builder.Add(id)) first ??= id;
        }
        ImmutableHashSet<MidoraId> materialized = builder.ToImmutable();
        bool changed = false;
        switch (mode)
        {
            case WorkspaceSelectionRangeMode.Replace:
                MidoraId? replacementPrimary = first;
                if (_ids.Count == materialized.Count
                    && _ids.SetEquals(materialized)
                    && Primary == replacementPrimary
                    && Anchor == replacementPrimary)
                {
                    return;
                }
                _ids = materialized;
                Primary = replacementPrimary;
                Anchor = Primary;
                changed = true;
                break;
            case WorkspaceSelectionRangeMode.Add:
                ImmutableHashSet<MidoraId> added = _ids.Union(materialized);
                changed = !ReferenceEquals(added, _ids);
                _ids = added;
                if (Primary is null && first is MidoraId firstAdded)
                {
                    Primary = firstAdded;
                    Anchor ??= Primary;
                    changed = true;
                }
                break;
            case WorkspaceSelectionRangeMode.Toggle:
                if (materialized.Count != 0)
                {
                    _ids = _ids.SymmetricExcept(materialized);
                    changed = true;
                }
                NormalizeEndpoints();
                break;
            case WorkspaceSelectionRangeMode.Remove:
                ImmutableHashSet<MidoraId> removed = _ids.Except(materialized);
                changed = !ReferenceEquals(removed, _ids);
                _ids = removed;
                NormalizeEndpoints();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (changed)
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
