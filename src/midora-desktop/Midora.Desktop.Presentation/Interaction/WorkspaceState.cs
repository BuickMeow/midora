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
    EventInstrumentEditor,
    MappingFunctionEditor
}

public readonly record struct WorkspaceKey(WorkspaceKind Kind, MidoraId? ObjectId)
{
    public static WorkspaceKey ForType(WorkspaceKind kind)
    {
        if (kind is WorkspaceKind.SegmentEditor
            or WorkspaceKind.EventInstrumentEditor
            or WorkspaceKind.MappingFunctionEditor)
        {
            throw new ArgumentException("This workspace kind requires an object identity.", nameof(kind));
        }
        return new(kind, null);
    }

    public static WorkspaceKey ForObject(WorkspaceKind kind, MidoraId objectId)
    {
        if (kind is not (WorkspaceKind.SegmentEditor
            or WorkspaceKind.EventInstrumentEditor
            or WorkspaceKind.MappingFunctionEditor))
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

public sealed class WorkspaceSelection
{
    private readonly HashSet<MidoraId> _ids = [];

    public IReadOnlyCollection<MidoraId> Ids => _ids;
    public MidoraId? Primary { get; private set; }
    public MidoraId? Anchor { get; private set; }

    public void Replace(MidoraId id)
    {
        Validate(id);
        _ids.Clear();
        _ids.Add(id);
        Primary = id;
        Anchor = id;
    }

    public void Add(MidoraId id, bool makePrimary = true)
    {
        Validate(id);
        _ids.Add(id);
        if (makePrimary || Primary is null)
        {
            Primary = id;
        }
        Anchor ??= id;
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
    }

    public void Clear()
    {
        _ids.Clear();
        Primary = null;
        Anchor = null;
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
