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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WholeProjectRejectsDamagedPlaceholderStableIdCollision(bool logicalTrack)
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        if (logicalTrack)
        {
            fixture.Project.DamagedLogicalTracks.Add(new(
                fixture.Track.Id,
                "Damaged Track",
                "tracks/damaged.pb",
                "hash mismatch",
                1));
        }
        else
        {
            fixture.Project.DamagedEventInstruments.Add(new(
                fixture.Instrument.Id,
                "Damaged Instrument",
                "instruments/damaged.pb",
                "hash mismatch",
                1));
        }

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest { EndTick = 480 });

        Assert.False(result.IsConsumable);
        Assert.Equal(CompilationFailureStage.SemanticValidation, result.FailureStage);
        Assert.Contains(result.Diagnostics, value =>
            value.Code == "MIDORA1003"
            && (logicalTrack
                ? value.Source.TrackId == fixture.Track.Id
                : value.Source.EventInstrumentId == fixture.Instrument.Id));
    }

    [Fact]
    public void ExplicitTrackScopeIgnoresUnrelatedDamagedPlaceholderStableIdCollisions()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        EventInstrument excludedInstrument = new(fixture.Project)
        {
            Name = "Excluded",
            TemplateLengthTicks = 480
        };
        excludedInstrument.SubVoices.Add(new SubVoice(fixture.Project));
        fixture.Project.EventInstruments.Add(excludedInstrument);
        LogicalTrack excludedTrack = new(fixture.Project)
        {
            Name = "Excluded",
            EventInstrumentId = excludedInstrument.Id
        };
        fixture.Project.Tracks.Add(excludedTrack);
        fixture.Project.DamagedEventInstruments.Add(new(
            excludedInstrument.Id,
            "Damaged Instrument",
            "instruments/damaged.pb",
            "hash mismatch",
            1));
        fixture.Project.DamagedLogicalTracks.Add(new(
            excludedTrack.Id,
            "Damaged Track",
            "tracks/damaged.pb",
            "hash mismatch",
            1));

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest
            {
                Purpose = CompilationPurpose.LogicalTrackAudioRender,
                EndTick = 480,
                IncludedTrackIds = [fixture.Track.Id]
            });

        Assert.True(result.IsConsumable);
        Assert.DoesNotContain(result.Diagnostics, value => value.Code == "MIDORA1003");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WholeProjectRejectsInvalidDamagedPlaceholderStableId(
        bool logicalTrack,
        bool zero)
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        MidoraId invalidId = zero
            ? default
            : MidoraId.FromSequence(fixture.Project.NextStableId);
        DamagedProjectObject placeholder = new(
            invalidId,
            "Damaged",
            logicalTrack ? "tracks/damaged.pb" : "instruments/damaged.pb",
            "hash mismatch",
            1);
        if (logicalTrack)
        {
            fixture.Project.DamagedLogicalTracks.Add(placeholder);
        }
        else
        {
            fixture.Project.DamagedEventInstruments.Add(placeholder);
        }

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest { EndTick = 480 });

        Assert.False(result.IsConsumable);
        Assert.Equal(CompilationFailureStage.SemanticValidation, result.FailureStage);
        Assert.Contains(result.Diagnostics, value => value.Code == "MIDORA1003");
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
