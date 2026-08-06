using Midora.Domain;

namespace Midora.Compiler.Tests;

public sealed class IncrementalCompilationTests
{
    [Fact]
    public void ChangedMiddleSegmentRecompilesDirtyRangeAndReusesConvergedSuffix()
    {
        MultiSegmentFixture fixture = CreateThreeSegmentProject();
        MidoraCompiler compiler = new();
        _ = compiler.CompileFull(fixture.Project);
        fixture.Notes[1].Note = 67;
        ProjectChangeSet changes = TrackChange(fixture.Track);

        CanonicalCompiledResult incremental = compiler.CompileIncremental(fixture.Project, changes);
        CanonicalCompiledResult full = new MidoraCompiler().CompileFull(fixture.Project);

        AssertFormallyEqual(full, incremental);
        Assert.Equal(1, compiler.LastTelemetry.RecompiledTrackCount);
        Assert.Equal(1, compiler.LastTelemetry.RecompiledSegmentCount);
        Assert.Equal(2, compiler.LastTelemetry.ReusedSegmentCount);
        Assert.Equal(1, compiler.LastTelemetry.StateConvergenceCount);
        Assert.Equal(480, compiler.LastTelemetry.EarliestDirtyTick);
    }

    [Fact]
    public void SourceOrderChangePreventsFalseStateConvergence()
    {
        MultiSegmentFixture fixture = CreateThreeSegmentProject();
        MidoraCompiler compiler = new();
        _ = compiler.CompileFull(fixture.Project);
        CompilerTestProject.AddNote(fixture.Segments[1], fixture.Instrument, 180, 120, 72);

        CanonicalCompiledResult incremental = compiler.CompileIncremental(
            fixture.Project, TrackChange(fixture.Track));
        CanonicalCompiledResult full = new MidoraCompiler().CompileFull(fixture.Project);

        AssertFormallyEqual(full, incremental);
        Assert.Equal(2, compiler.LastTelemetry.RecompiledSegmentCount);
        Assert.Equal(1, compiler.LastTelemetry.ReusedSegmentCount);
        Assert.Equal(0, compiler.LastTelemetry.StateConvergenceCount);
    }

    [Fact]
    public void DeletingEmptySegmentConvergesAtFollowingCheckpointWithoutRecompilingSuffix()
    {
        MultiSegmentFixture fixture = CreateThreeSegmentProject();
        fixture.Segments[1].Notes.Clear();
        MidoraCompiler compiler = new();
        _ = compiler.CompileFull(fixture.Project);
        fixture.Track.Segments.Remove(fixture.Segments[1]);

        CanonicalCompiledResult incremental = compiler.CompileIncremental(
            fixture.Project, TrackChange(fixture.Track));
        CanonicalCompiledResult full = new MidoraCompiler().CompileFull(fixture.Project);

        AssertFormallyEqual(full, incremental);
        Assert.Equal(0, compiler.LastTelemetry.RecompiledSegmentCount);
        Assert.Equal(2, compiler.LastTelemetry.ReusedSegmentCount);
        Assert.Equal(1, compiler.LastTelemetry.StateConvergenceCount);
        Assert.Equal(480, compiler.LastTelemetry.EarliestDirtyTick);
    }

    [Fact]
    public void InstrumentContextChangeInvalidatesEveryDependentSegment()
    {
        MultiSegmentFixture fixture = CreateThreeSegmentProject();
        MidoraCompiler compiler = new();
        _ = compiler.CompileFull(fixture.Project);
        fixture.Instrument.RootNote = 57;
        ProjectChangeSet changes = new();
        changes.EventInstrumentIds.Add(fixture.Instrument.Id);

        CanonicalCompiledResult incremental = compiler.CompileIncremental(fixture.Project, changes);
        CanonicalCompiledResult full = new MidoraCompiler().CompileFull(fixture.Project);

        AssertFormallyEqual(full, incremental);
        Assert.Equal(3, compiler.LastTelemetry.RecompiledSegmentCount);
        Assert.Equal(0, compiler.LastTelemetry.ReusedSegmentCount);
        Assert.Equal(0, compiler.LastTelemetry.StateConvergenceCount);
        Assert.Equal(0, compiler.LastTelemetry.EarliestDirtyTick);
    }

    [Fact]
    public void ExplicitInvalidationWithUnchangedFingerprintVerifiesOneRegionBeforeConvergence()
    {
        MultiSegmentFixture fixture = CreateThreeSegmentProject();
        MidoraCompiler compiler = new();
        CanonicalCompiledResult full = compiler.CompileFull(fixture.Project);

        CanonicalCompiledResult incremental = compiler.CompileIncremental(
            fixture.Project, TrackChange(fixture.Track));

        AssertFormallyEqual(full, incremental);
        Assert.Equal(1, compiler.LastTelemetry.RecompiledSegmentCount);
        Assert.Equal(2, compiler.LastTelemetry.ReusedSegmentCount);
        Assert.Equal(1, compiler.LastTelemetry.StateConvergenceCount);
    }

    [Fact]
    public void ReorderedSegmentCollectionDoesNotChangeCanonicalForm()
    {
        MultiSegmentFixture fixture = CreateThreeSegmentProject();
        MidoraCompiler compiler = new();
        CanonicalCompiledResult before = compiler.CompileFull(fixture.Project);
        fixture.Track.Segments.Reverse();

        CanonicalCompiledResult incremental = compiler.CompileIncremental(
            fixture.Project, TrackChange(fixture.Track));
        CanonicalCompiledResult full = new MidoraCompiler().CompileFull(fixture.Project);

        AssertFormallyEqual(before, incremental);
        AssertFormallyEqual(full, incremental);
    }

    [Fact]
    public void ClearCacheForcesAllSegmentsToRecompile()
    {
        MultiSegmentFixture fixture = CreateThreeSegmentProject();
        MidoraCompiler compiler = new();
        _ = compiler.CompileFull(fixture.Project);
        compiler.ClearCache();

        CanonicalCompiledResult incremental = compiler.CompileIncremental(
            fixture.Project, new ProjectChangeSet());
        CanonicalCompiledResult full = new MidoraCompiler().CompileFull(fixture.Project);

        AssertFormallyEqual(full, incremental);
        Assert.Equal(3, compiler.LastTelemetry.RecompiledSegmentCount);
        Assert.Equal(0, compiler.LastTelemetry.ReusedSegmentCount);
    }

    [Fact]
    public void RangeRequestReusesRawCheckpointsButRebuildsFormalRangeResult()
    {
        MultiSegmentFixture fixture = CreateThreeSegmentProject();
        MidoraCompiler compiler = new();
        _ = compiler.CompileFull(fixture.Project);
        CompilationRequest request = new()
        {
            Purpose = CompilationPurpose.Range,
            StartTick = 540,
            EndTick = 1_140
        };

        CanonicalCompiledResult incremental = compiler.CompileIncremental(
            fixture.Project, new ProjectChangeSet(), request);
        CanonicalCompiledResult full = new MidoraCompiler().CompileFull(fixture.Project, request);

        AssertFormallyEqual(full, incremental);
        Assert.Equal(0, compiler.LastTelemetry.RecompiledTrackCount);
        Assert.Equal(1, compiler.LastTelemetry.ReusedTrackCount);
        Assert.Equal(3, compiler.LastTelemetry.ReusedSegmentCount);
    }

    [Fact]
    public void RandomLegalEditsRemainFormallyEqualToFullCompileOracle()
    {
        MultiSegmentFixture fixture = CreateThreeSegmentProject();
        MidoraCompiler compiler = new();
        _ = compiler.CompileFull(fixture.Project);
        Random random = new(0x4D49444F);

        for (int iteration = 0; iteration < 80; iteration++)
        {
            int segmentIndex = random.Next(fixture.Segments.Length);
            Segment segment = fixture.Segments[segmentIndex];
            LogicalNote note = segment.Notes[random.Next(segment.Notes.Count)];
            switch (random.Next(4))
            {
                case 0:
                    note.Note = random.Next(24, 108);
                    break;
                case 1:
                    note.Velocity = random.Next(1, 128);
                    break;
                case 2:
                    note.StartTick = random.Next(0, 241);
                    break;
                default:
                    note.LengthTicks = random.Next(1, 241);
                    break;
            }
            if ((iteration & 7) == 0)
            {
                segment.Notes.Reverse();
            }

            CanonicalCompiledResult incremental = compiler.CompileIncremental(
                fixture.Project, TrackChange(fixture.Track));
            CanonicalCompiledResult full = new MidoraCompiler().CompileFull(fixture.Project);
            AssertFormallyEqual(full, incremental);
        }
    }

    private static MultiSegmentFixture CreateThreeSegmentProject()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 360);
        fixture.Instrument.TemplateLengthTicks = 240;
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 240, 60, 100));
        Segment[] segments = new Segment[3];
        LogicalNote[] notes = new LogicalNote[3];
        segments[0] = fixture.Segment;
        notes[0] = CompilerTestProject.AddNote(segments[0], fixture.Instrument, 0, 180, 60);
        for (int index = 1; index < segments.Length; index++)
        {
            Segment segment = new(fixture.Project)
            {
                ProjectStartTick = index * 480,
                LengthTicks = 360
            };
            CompilerTestProject.RegisterSegment(fixture.Project, segment);
            fixture.Track.Segments.Add(segment);
            segments[index] = segment;
            notes[index] = CompilerTestProject.AddNote(segment, fixture.Instrument, 60, 180, 60 + index);
        }
        return new(
            fixture.Project,
            fixture.Track,
            fixture.Instrument,
            segments,
            notes);
    }

    private static ProjectChangeSet TrackChange(LogicalTrack track)
    {
        ProjectChangeSet result = new();
        result.TrackIds.Add(track.Id);
        return result;
    }

    private static void AssertFormallyEqual(
        CanonicalCompiledResult expected,
        CanonicalCompiledResult actual)
    {
        Assert.Equal(expected.IsConsumable, actual.IsConsumable);
        Assert.Equal(expected.IsPartial, actual.IsPartial);
        Assert.Equal(expected.FailureStage, actual.FailureStage);
        Assert.Equal(expected.Context.Purpose, actual.Context.Purpose);
        Assert.Equal(expected.Context.StartTick, actual.Context.StartTick);
        Assert.Equal(expected.Context.RequestedEndTick, actual.Context.RequestedEndTick);
        Assert.Equal(expected.Context.EndTick, actual.Context.EndTick);
        Assert.Equal(expected.Context.EndTickSource, actual.Context.EndTickSource);
        Assert.Equal(expected.Context.IncludesAllTracks, actual.Context.IncludesAllTracks);
        Assert.Equal(expected.Context.IncludesAllSubVoices, actual.Context.IncludesAllSubVoices);
        Assert.Equal(expected.Context.TreatWarningsAsErrors, actual.Context.TreatWarningsAsErrors);
        Assert.Equal(expected.Context.IncludedTrackIds.ToArray(), actual.Context.IncludedTrackIds.ToArray());
        Assert.Equal(expected.Context.IncludedSubVoiceIds.ToArray(), actual.Context.IncludedSubVoiceIds.ToArray());
        Assert.Equal(expected.StartTick, actual.StartTick);
        Assert.Equal(expected.EndTick, actual.EndTick);
        Assert.Equal(expected.Fingerprint, actual.Fingerprint);
        AssertStatisticsEqual(expected.Statistics, actual.Statistics);
        Assert.Equal(expected.Events.ToArray(), actual.Events.ToArray());
        Assert.Equal(expected.Allocations.ToArray(), actual.Allocations.ToArray());
        Assert.Equal(expected.Conductor.Tempos.ToArray(), actual.Conductor.Tempos.ToArray());
        Assert.Equal(expected.Conductor.TimeSignatures.ToArray(), actual.Conductor.TimeSignatures.ToArray());
        Assert.Equal(expected.Conductor.KeySignatures.ToArray(), actual.Conductor.KeySignatures.ToArray());
        Assert.Equal(expected.Conductor.Markers.ToArray(), actual.Conductor.Markers.ToArray());
        Assert.Equal(expected.Conductor.EndMarker, actual.Conductor.EndMarker);
        Assert.Equal(expected.Diagnostics, actual.Diagnostics);
    }

    private static void AssertStatisticsEqual(
        CompilationStatistics expected,
        CompilationStatistics actual)
    {
        Assert.Equal(expected.SourceTrackCount, actual.SourceTrackCount);
        Assert.Equal(expected.ExpandedInstanceCount, actual.ExpandedInstanceCount);
        Assert.Equal(expected.EventCount, actual.EventCount);
        Assert.Equal(expected.PeakChannelUnitCount, actual.PeakChannelUnitCount);
        Assert.Equal(expected.ExpandedSegmentCount, actual.ExpandedSegmentCount);
        Assert.Equal(
            expected.ParticipatingEventInstrumentCount,
            actual.ParticipatingEventInstrumentCount);
        Assert.Equal(expected.ParticipatingSubVoiceCount, actual.ParticipatingSubVoiceCount);
        Assert.Equal(expected.UsedPortCount, actual.UsedPortCount);
        if (expected.ResourceShortage is null)
        {
            Assert.Null(actual.ResourceShortage);
            return;
        }

        ResourceShortageDetails shortage = Assert.IsType<ResourceShortageDetails>(actual.ResourceShortage);
        Assert.Equal(expected.ResourceShortage.Range, shortage.Range);
        Assert.Equal(
            expected.ResourceShortage.RequestedChannelUnitCount,
            shortage.RequestedChannelUnitCount);
        Assert.Equal(
            expected.ResourceShortage.AvailableChannelUnitCount,
            shortage.AvailableChannelUnitCount);
        Assert.Equal(expected.ResourceShortage.TrackIds.ToArray(), shortage.TrackIds.ToArray());
        Assert.Equal(expected.ResourceShortage.SegmentIds.ToArray(), shortage.SegmentIds.ToArray());
        Assert.Equal(
            expected.ResourceShortage.LogicalNoteIds.ToArray(),
            shortage.LogicalNoteIds.ToArray());
        Assert.Equal(
            expected.ResourceShortage.EventInstrumentIds.ToArray(),
            shortage.EventInstrumentIds.ToArray());
        Assert.Equal(expected.ResourceShortage.SubVoiceIds.ToArray(), shortage.SubVoiceIds.ToArray());
    }

    private sealed record MultiSegmentFixture(
        MidoraProject Project,
        LogicalTrack Track,
        EventInstrument Instrument,
        Segment[] Segments,
        LogicalNote[] Notes);
}
