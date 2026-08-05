using Midora.Domain;

namespace Midora.Compiler.Tests;

internal static class CompilerTestProject
{
    public static (MidoraProject Project, LogicalTrack Track, Segment Segment, EventInstrument Instrument, SubVoice Voice) Create(
        int subVoiceCount = 1,
        long segmentLength = 1920)
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new()
        {
            Name = "Piano",
            RootNote = 60,
            TemplateLengthTicks = 480,
            OverlapPolicy = OverlapPolicy.Warn
        };
        for (int i = 0; i < subVoiceCount; i++)
        {
            instrument.SubVoices.Add(new SubVoice { Name = $"Voice {i + 1}" });
        }
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new() { Name = "Track", EventInstrumentId = instrument.Id };
        Segment segment = new() { ProjectStartTick = 0, LengthTicks = segmentLength };
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        return (project, track, segment, instrument, instrument.SubVoices[0]);
    }

    public static LogicalNote AddNote(Segment segment, EventInstrument instrument, long start, long length, int note = 60)
    {
        LogicalNote value = new()
        {
            StartTick = start,
            LengthTicks = length,
            Note = note,
            Velocity = 110
        };
        segment.Notes.Add(value);
        return value;
    }
}
