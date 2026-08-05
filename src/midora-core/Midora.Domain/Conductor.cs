namespace Midora.Domain;

public sealed record TempoChange(long Tick, decimal BeatsPerMinute)
{
    public MidoraId Id { get; init; } = MidoraId.New();
}

public sealed record TimeSignatureChange(long Tick, int Numerator, int Denominator)
{
    public MidoraId Id { get; init; } = MidoraId.New();
}

public sealed record KeySignatureChange(long Tick, int SharpsFlats, bool IsMinor)
{
    public MidoraId Id { get; init; } = MidoraId.New();
}

public readonly record struct ProjectMarker(MidoraId Id, long Tick, string Name);

public sealed class ConductorTrack
{
    public List<TempoChange> Tempos { get; } = [new(0, 120m)];
    public List<TimeSignatureChange> TimeSignatures { get; } = [new(0, 4, 4)];
    public List<KeySignatureChange> KeySignatures { get; } = [];
    public List<ProjectMarker> Markers { get; } = [];
    public long? EndMarkerTick { get; set; }
}
