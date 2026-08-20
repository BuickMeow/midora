using Midora.Domain;

namespace Midora.AudioRender.Tests;

internal static class AudioRenderTestProject
{
    public static MidoraProject Create(params (string Name, long LengthTicks, byte Note)[] tracks)
    {
        MidoraProject project = new(192);
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            RootNote = 60,
            TemplateLengthTicks = 192,
            OverlapPolicy = OverlapPolicy.Warn
        };
        SubVoice voice = new(project) { Name = "Voice" };
        voice.Events.Add(TemplateEvent.Note(project, 0, 96, 60, 100));
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);

        foreach ((string name, long lengthTicks, byte note) in tracks)
        {
            LogicalTrack track = new(project) {
                Name = name,
            };
            ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
            Segment segment = new(project) { LengthTicks = lengthTicks };
            segment.Notes.Add(new(project)
            {
                LengthTicks = lengthTicks,
                Note = note,
                Velocity = 100
            });
            track.Segments.Add(segment);
        }
        return project;
    }

    public static string CreateOwnedDirectory(string prefix = "midora-audio-render-test")
    {
        string path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
