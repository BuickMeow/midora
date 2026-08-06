using Midora.Domain;

namespace Midora.Compiler.Tests;

public sealed class DamagedPlaceholderCompilationTests
{
    [Fact]
    public void TrackBoundToDamagedInstrumentFailsOnlyWhenItParticipates()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);
        MidoraId damagedInstrumentId = fixture.Project.AllocateStableId();
        fixture.Project.DamagedEventInstruments.Add(new(
            damagedInstrumentId,
            "Damaged",
            "instruments/damaged.pb",
            "hash mismatch",
            1));
        LogicalTrack damagedTrack = new(fixture.Project)
        {
            Name = "Damaged binding",
            EventInstrumentId = damagedInstrumentId
        };
        Segment damagedSegment = new(fixture.Project) { LengthTicks = 480 };
        damagedSegment.Notes.Add(new LogicalNote(fixture.Project)
        {
            LengthTicks = 120,
            Note = 64,
            Velocity = 100
        });
        damagedTrack.Segments.Add(damagedSegment);
        fixture.Project.Tracks.Add(damagedTrack);

        CanonicalCompiledResult wholeProject = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest { EndTick = 480 });
        CanonicalCompiledResult healthySelection = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest
            {
                Purpose = CompilationPurpose.LogicalTrackAudioRender,
                EndTick = 480,
                IncludedTrackIds = [fixture.Track.Id]
            });
        CanonicalCompiledResult damagedSelection = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest
            {
                Purpose = CompilationPurpose.LogicalTrackAudioRender,
                EndTick = 480,
                IncludedTrackIds = [damagedTrack.Id]
            });

        AssertDamagedBindingFailure(wholeProject, damagedTrack.Id, damagedInstrumentId);
        Assert.True(healthySelection.IsConsumable);
        Assert.DoesNotContain(healthySelection.Diagnostics, value =>
            value.Source.TrackId == damagedTrack.Id
            || value.Source.EventInstrumentId == damagedInstrumentId);
        AssertDamagedBindingFailure(damagedSelection, damagedTrack.Id, damagedInstrumentId);
    }

    [Fact]
    public void OrdinaryBrokenInstrumentReferenceRemainsNonBlockingInformation()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        MidoraId missingInstrumentId = MidoraId.FromSequence(fixture.Project.NextStableId + 100);
        fixture.Track.EventInstrumentId = missingInstrumentId;
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 120);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest { EndTick = 480 });

        Assert.True(result.IsConsumable);
        Assert.Empty(result.Events.ToArray());
        Assert.Contains(result.Diagnostics, value =>
            value.Code == "MIDORA1303"
            && value.Severity == DiagnosticSeverity.Info
            && value.Source.TrackId == fixture.Track.Id);
        Assert.DoesNotContain(result.Diagnostics, value => value.Code == "MIDORA1305");
    }

    private static void AssertDamagedBindingFailure(
        CanonicalCompiledResult result,
        MidoraId trackId,
        MidoraId instrumentId)
    {
        Assert.False(result.IsConsumable);
        Assert.True(result.IsPartial);
        Assert.Equal(CompilationFailureStage.SemanticValidation, result.FailureStage);
        Assert.Contains(result.Diagnostics, value =>
            value.Code == "MIDORA1305"
            && value.Severity == DiagnosticSeverity.Error
            && value.Source.TrackId == trackId
            && value.Source.EventInstrumentId == instrumentId);
        Assert.DoesNotContain(result.Diagnostics, value =>
            value.Source.TrackId == trackId
            && value.Code is "MIDORA1303" or "MIDORA1304");
    }
}
