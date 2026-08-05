namespace Midora.Domain;

public sealed record TempoChange
{
    public TempoChange(MidoraProject project, long tick, decimal beatsPerMinute)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
        Tick = tick;
        BeatsPerMinute = beatsPerMinute;
    }

    public MidoraId Id { get; init; }
    public long Tick { get; init; }
    public decimal BeatsPerMinute { get; init; }
}

public sealed record TimeSignatureChange
{
    public TimeSignatureChange(MidoraProject project, long tick, int numerator, int denominator)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
        Tick = tick;
        Numerator = numerator;
        Denominator = denominator;
    }

    public MidoraId Id { get; init; }
    public long Tick { get; init; }
    public int Numerator { get; init; }
    public int Denominator { get; init; }
}

public sealed record KeySignatureChange
{
    public KeySignatureChange(MidoraProject project, long tick, int sharpsFlats, bool isMinor)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
        Tick = tick;
        SharpsFlats = sharpsFlats;
        IsMinor = isMinor;
    }

    public MidoraId Id { get; init; }
    public long Tick { get; init; }
    public int SharpsFlats { get; init; }
    public bool IsMinor { get; init; }
}

public sealed record ProjectMarker
{
    public ProjectMarker(MidoraProject project, long tick, string name)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
        Tick = tick;
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    public MidoraId Id { get; init; }
    public long Tick { get; init; }
    public string Name { get; init; }
}

public sealed class ProjectEndMarker
{
    public ProjectEndMarker(MidoraProject project, long tick)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
        Tick = tick;
    }

    public MidoraId Id { get; init; }
    public long Tick { get; set; }
}

public sealed class ConductorTrack
{
    internal ConductorTrack(MidoraProject project, bool createInitialState)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (createInitialState)
        {
            Tempos.Add(new TempoChange(project, 0, 120m));
            TimeSignatures.Add(new TimeSignatureChange(project, 0, 4, 4));
        }
    }

    public List<TempoChange> Tempos { get; } = [];
    public List<TimeSignatureChange> TimeSignatures { get; } = [];
    public List<KeySignatureChange> KeySignatures { get; } = [];
    public List<ProjectMarker> Markers { get; } = [];
    public ProjectEndMarker? EndMarker { get; set; }
    public long? EndMarkerTick => EndMarker?.Tick;
}
