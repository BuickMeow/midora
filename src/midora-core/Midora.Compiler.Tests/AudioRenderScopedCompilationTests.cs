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

    [Fact]
    public void ExcludedTrackStableIdCorruptionDoesNotPoisonScopedCompilation()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);
        LogicalTrack excludedTrack = new(fixture.Project)
        {
            Name = "Excluded",
            EventInstrumentId = fixture.Instrument.Id
        };
        Segment excludedSegment = new(fixture.Project) { LengthTicks = 480 };
        LogicalNote duplicatedNote = new(fixture.Project)
        {
            LengthTicks = 120,
            Note = 64,
            Velocity = 100
        };
        excludedSegment.Notes.Add(duplicatedNote);
        excludedSegment.Notes.Add(duplicatedNote);
        excludedTrack.Segments.Add(excludedSegment);
        fixture.Project.Tracks.Add(excludedTrack);

        CanonicalCompiledResult wholeProject = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest { EndTick = 480 });
        CanonicalCompiledResult selected = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest
            {
                Purpose = CompilationPurpose.LogicalTrackAudioRender,
                EndTick = 480,
                IncludedTrackIds = [fixture.Track.Id]
            });

        Assert.False(wholeProject.IsConsumable);
        Assert.Contains(wholeProject.Diagnostics, value =>
            value.Code == "MIDORA1003"
            && value.Source.TrackId == excludedTrack.Id);
        Assert.True(selected.IsConsumable);
        Assert.DoesNotContain(selected.Diagnostics, value =>
            value.Code == "MIDORA1003"
            || value.Source.TrackId == excludedTrack.Id);
    }

    [Fact]
    public void UnusedInstrumentStableIdCorruptionDoesNotPoisonTrackSelection()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);
        EventInstrument unusedInstrument = new(fixture.Project)
        {
            Name = "Unused",
            TemplateLengthTicks = 480
        };
        SubVoice duplicatedVoice = new(fixture.Project);
        unusedInstrument.SubVoices.Add(duplicatedVoice);
        unusedInstrument.SubVoices.Add(duplicatedVoice);
        fixture.Project.EventInstruments.Add(unusedInstrument);

        CanonicalCompiledResult wholeProject = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest { EndTick = 480 });
        CanonicalCompiledResult selected = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest
            {
                Purpose = CompilationPurpose.LogicalTrackAudioRender,
                EndTick = 480,
                IncludedTrackIds = [fixture.Track.Id]
            });

        Assert.False(wholeProject.IsConsumable);
        Assert.Contains(wholeProject.Diagnostics, value =>
            value.Code == "MIDORA1003"
            && value.Source.EventInstrumentId == unusedInstrument.Id);
        Assert.True(selected.IsConsumable);
        Assert.DoesNotContain(selected.Diagnostics, value =>
            value.Source.EventInstrumentId == unusedInstrument.Id);
    }

    [Fact]
    public void UnusedInstrumentBrokenFolderReferenceDoesNotPoisonTrackSelection()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);
        EventInstrument unusedInstrument = new(fixture.Project)
        {
            Name = "Unused",
            TemplateLengthTicks = 480,
            LibraryFolderId = MidoraId.FromSequence(fixture.Project.NextStableId + 100)
        };
        unusedInstrument.SubVoices.Add(new SubVoice(fixture.Project));
        fixture.Project.EventInstruments.Add(unusedInstrument);

        CanonicalCompiledResult wholeProject = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest { EndTick = 480 });
        CanonicalCompiledResult selected = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest
            {
                Purpose = CompilationPurpose.LogicalTrackAudioRender,
                EndTick = 480,
                IncludedTrackIds = [fixture.Track.Id],
                TreatWarningsAsErrors = true
            });

        Assert.True(wholeProject.IsConsumable);
        Assert.Contains(wholeProject.Diagnostics, value =>
            value.Code == "MIDORA1021"
            && value.Source.EventInstrumentId == unusedInstrument.Id);
        Assert.True(selected.IsConsumable);
        Assert.DoesNotContain(selected.Diagnostics, value =>
            value.Code == "MIDORA1021"
            || value.Source.EventInstrumentId == unusedInstrument.Id);
    }

    [Fact]
    public void UnusedInvalidFolderDefinitionAndStableIdDoNotPoisonTrackSelection()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        EventInstrument unusedInstrument = new(fixture.Project)
        {
            Name = "Unused",
            TemplateLengthTicks = 480
        };
        unusedInstrument.SubVoices.Add(new SubVoice(fixture.Project));
        fixture.Project.EventInstruments.Add(unusedInstrument);
        EventInstrumentLibraryFolder invalidFolder = new(fixture.Project)
        {
            Id = unusedInstrument.Id,
            Name = "Unfiled"
        };
        fixture.Project.EventInstrumentFolders.Add(invalidFolder);

        CanonicalCompiledResult wholeProject = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest { EndTick = 480 });
        CanonicalCompiledResult selected = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest
            {
                Purpose = CompilationPurpose.LogicalTrackAudioRender,
                EndTick = 480,
                IncludedTrackIds = [fixture.Track.Id]
            });

        Assert.False(wholeProject.IsConsumable);
        Assert.Contains(wholeProject.Diagnostics, value => value.Code == "MIDORA1020");
        Assert.Contains(wholeProject.Diagnostics, value => value.Code == "MIDORA1003");
        Assert.True(selected.IsConsumable);
        Assert.DoesNotContain(selected.Diagnostics, value =>
            value.Code is "MIDORA1020" or "MIDORA1003");
    }

    [Fact]
    public void ParticipatingInvalidFolderDefinitionStillFailsTrackSelection()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        EventInstrumentLibraryFolder invalidFolder = new(fixture.Project)
        {
            Name = "Unfiled"
        };
        fixture.Project.EventInstrumentFolders.Add(invalidFolder);
        fixture.Instrument.LibraryFolderId = invalidFolder.Id;

        CanonicalCompiledResult selected = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest
            {
                Purpose = CompilationPurpose.LogicalTrackAudioRender,
                EndTick = 480,
                IncludedTrackIds = [fixture.Track.Id]
            });

        Assert.False(selected.IsConsumable);
        Assert.Equal(CompilationFailureStage.SemanticValidation, selected.FailureStage);
        Assert.Contains(selected.Diagnostics, value => value.Code == "MIDORA1020");
    }

    [Fact]
    public void UnusedDuplicateInstrumentIdDoesNotPoisonOrThrowFromTrackSelection()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);
        EventInstrument firstUnused = new(fixture.Project)
        {
            Name = "Unused A",
            TemplateLengthTicks = 480
        };
        firstUnused.SubVoices.Add(new SubVoice(fixture.Project));
        EventInstrument secondUnused = new(fixture.Project)
        {
            Id = firstUnused.Id,
            Name = "Unused B",
            TemplateLengthTicks = 480
        };
        secondUnused.SubVoices.Add(new SubVoice(fixture.Project));
        fixture.Project.EventInstruments.Add(firstUnused);
        fixture.Project.EventInstruments.Add(secondUnused);

        CanonicalCompiledResult wholeProject = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest { EndTick = 480 });
        CanonicalCompiledResult selected = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest
            {
                Purpose = CompilationPurpose.LogicalTrackAudioRender,
                EndTick = 480,
                IncludedTrackIds = [fixture.Track.Id]
            });

        Assert.False(wholeProject.IsConsumable);
        Assert.Contains(wholeProject.Diagnostics, value =>
            value.Code == "MIDORA1201"
            && value.Source.EventInstrumentId == firstUnused.Id);
        Assert.True(selected.IsConsumable);
        Assert.DoesNotContain(selected.Diagnostics, value =>
            value.Code is "MIDORA1003" or "MIDORA1201"
            && value.Source.EventInstrumentId == firstUnused.Id);
    }
}
