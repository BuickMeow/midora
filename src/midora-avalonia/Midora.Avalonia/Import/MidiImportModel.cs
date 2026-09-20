namespace Midora.Avalonia.Import;

public enum ImportedMidiDiagnosticSeverity : byte
{
    Info,
    Warning,
}

public sealed record ImportedMidiDiagnostic(
    ImportedMidiDiagnosticSeverity Severity,
    string Code,
    string Message,
    int? SourceTrackIndex = null,
    long? Tick = null);

public enum ImportedMidiEventKind : byte
{
    ControlChange,
    ProgramChange,
    PitchBend,
    AfterTouch,
    PolyAfterTouch,
    ChannelMode,
}

public enum ImportedConductorKind : byte
{
    Tempo,
    TimeSignature,
    Marker,
    KeySignature,
}

public readonly record struct ImportedMidiNote(
    long StartTick,
    long EndTick,
    int Key,
    int Velocity,
    int NoteOffVelocity,
    int Channel)
{
    public long LengthTicks => EndTick - StartTick;
}

public readonly record struct ImportedMidiEvent(
    long Tick,
    int Channel,
    ImportedMidiEventKind Kind,
    int Data1,
    int Data2)
{
    public int RawStatus { get; init; }

    public int PitchBendValue => Data1 | (Data2 << 7);
}

public readonly record struct ImportedMidiConductorEvent(
    long Tick,
    ImportedConductorKind Kind,
    double Value,
    int Numerator,
    int Denominator,
    string Text);

public sealed record ImportedMidiTrack(
    int SourceTrackIndex,
    string Name,
    int ChannelMask,
    long EndTick,
    IReadOnlyList<ImportedMidiNote> Notes,
    IReadOnlyList<ImportedMidiEvent> Events);

public sealed record ImportedMidiProject(
    ushort Format,
    int TicksPerQuarterNote,
    long MaximumEndTick,
    IReadOnlyList<ImportedMidiTrack> Tracks,
    IReadOnlyList<ImportedMidiConductorEvent> Conductor)
{
    public string SourceFileName { get; init; } = string.Empty;

    public IReadOnlyList<ImportedMidiDiagnostic> Diagnostics { get; init; } = [];

    public static ImportedMidiProject Parse(string path) => MidiImporter.Import(path);

    public static ImportedMidiProject Parse(ReadOnlySpan<byte> bytes, string sourceFileName)
        => MidiImporter.Import(bytes, sourceFileName);
}

public sealed class MidiImportException : Exception
{
    public MidiImportException(string message)
        : base(message)
    {
    }

    public MidiImportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
