using Midora.Domain;
using System.Runtime.CompilerServices;

namespace Midora.Compiler.Tests;

internal static class CompilerTestProject
{
    private static readonly ConditionalWeakTable<Segment, MidoraProject> ProjectsBySegment = new();

    public static (MidoraProject Project, LogicalTrack Track, Segment Segment, EventInstrument Instrument, SubVoice Voice) Create(
        int subVoiceCount = 1,
        long segmentLength = 1920)
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project)
        {
            Name = "Piano",
            RootNote = 60,
            TemplateLengthTicks = 480,
            OverlapPolicy = OverlapPolicy.Warn
        };
        for (int i = 0; i < subVoiceCount; i++)
        {
            instrument.SubVoices.Add(new SubVoice(project) { Name = $"Voice {i + 1}" });
        }
        project.EventInstruments.Add(instrument);
        EventInstrumentUsage usage = new(project)
        {
            EventInstrumentId = instrument.Id
        };
        project.EventInstrumentUsages.Add(usage);
        LogicalTrack track = new(project)
        {
            Name = "Track",
            EventInstrumentUsageId = usage.Id
        };
        Segment segment = new(project) { ProjectStartTick = 0, LengthTicks = segmentLength };
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, track.Id));
        ProjectsBySegment.Add(segment, project);
        return (project, track, segment, instrument, instrument.SubVoices[0]);
    }

    public static LogicalNote AddNote(
        Segment segment,
        EventInstrument instrument,
        long start,
        long length,
        int note = 60)
    {
        MidoraProject project = ProjectsBySegment.GetValue(segment,
            _ => throw new InvalidOperationException("Register the test Segment before adding a note."));
        LogicalNote value = new(project)
        {
            StartTick = start,
            LengthTicks = length,
            Note = note,
            Velocity = 110
        };
        segment.Notes.Add(value);
        return value;
    }

    public static void RegisterSegment(MidoraProject project, Segment segment) =>
        ProjectsBySegment.Add(segment, project);
}
