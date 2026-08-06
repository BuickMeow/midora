using Midora.Domain;

namespace Midora.Compiler.Tests;

public sealed class AudioRenderScopedCompilationTests
{
    [Fact]
    public void ExcludedTrackContentErrorDoesNotPoisonIndependentTrackRenderCompilation()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);
        LogicalTrack invalidTrack = new(fixture.Project)
        {
            Name = "Invalid",
            EventInstrumentId = fixture.Instrument.Id
        };
        Segment invalidSegment = new(fixture.Project) { LengthTicks = 480 };
        invalidSegment.Notes.Add(new(fixture.Project)
        {
            LengthTicks = 240,
            Note = 60,
            Velocity = 200
        });
        invalidTrack.Segments.Add(invalidSegment);
        fixture.Project.Tracks.Add(invalidTrack);

        CanonicalCompiledResult selected = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest
            {
                Purpose = CompilationPurpose.LogicalTrackAudioRender,
                EndTick = 480,
                IncludedTrackIds = [fixture.Track.Id],
                TreatWarningsAsErrors = true
            });
        CanonicalCompiledResult invalid = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest
            {
                Purpose = CompilationPurpose.LogicalTrackAudioRender,
                EndTick = 480,
                IncludedTrackIds = [invalidTrack.Id]
            });

        Assert.True(selected.IsConsumable);
        Assert.DoesNotContain(selected.Diagnostics, value =>
            value.Source.TrackId == invalidTrack.Id);
        Assert.False(invalid.IsConsumable);
        Assert.Contains(invalid.Diagnostics, value =>
            value.Source.TrackId == invalidTrack.Id
            && value.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void NaturalEndUsesOnlyTheCompileContextTrackSelection()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);
        LogicalTrack laterTrack = new(fixture.Project)
        {
            Name = "Later",
            EventInstrumentId = fixture.Instrument.Id
        };
        Segment laterSegment = new(fixture.Project)
        {
            ProjectStartTick = 10_000,
            LengthTicks = 480
        };
        laterTrack.Segments.Add(laterSegment);
        fixture.Project.Tracks.Add(laterTrack);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest
            {
                Purpose = CompilationPurpose.LogicalTrackAudioRender,
                IncludedTrackIds = [fixture.Track.Id]
            });

        Assert.True(result.IsConsumable);
        Assert.Equal(240, result.EndTick);
    }

    [Fact]
    public void HiddenSegmentContentDoesNotExtendNaturalEnd()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        fixture.Segment.ContentOffsetTick = 100;
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest
            {
                Purpose = CompilationPurpose.LogicalTrackAudioRender,
                IncludedTrackIds = [fixture.Track.Id]
            });

        Assert.True(result.IsConsumable);
        Assert.Equal(0, result.EndTick);
        Assert.Empty(result.Events.ToArray());
    }
}
