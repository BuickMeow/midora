namespace Midora.Avalonia.Presentation.Controls;

/// <summary>
/// Port of the mode/kind enums that live inside the WPF <c>TimelineSurface.cs</c>. Kept in a
/// dedicated file so the ported timeline surface and the interaction policy share them.
/// </summary>
public enum TimelineSurfaceMode
{
    General,
    Arrangement,
    PianoRoll,
    EventLanes,
    Conductor,
    Velocity
}

public enum TimelineToolMode
{
    Select,
    Draw,
    Erase,
    Split
}

public enum TimelineItemEditKind
{
    Move,
    ResizeStart,
    ResizeEnd
}

/// <summary>Identifies an arrangement Segment activation for workspace navigation.</summary>
public readonly record struct TimelineSegmentActivation(int Lane, long StartTick);
