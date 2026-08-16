using Midora.Domain;

namespace Midora.Compiler.Tests;

public sealed class ResourceStatisticsTests
{
    [Fact]
    public void AllocationIdentifiesEachInstanceAndSharedChannelGroup()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 960);
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        LogicalNote first = CompilerTestProject.AddNote(
            fixture.Segment,
            fixture.Instrument,
            0,
            240);
        LogicalNote second = CompilerTestProject.AddNote(
            fixture.Segment,
            fixture.Instrument,
            120,
            240);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);
        ChannelUnitAllocation[] allocations = result.Allocations.ToArray();

        Assert.True(result.IsConsumable);
        Assert.Equal(2, allocations.Length);
        Assert.Equal([first.Id, second.Id], allocations.Select(value => value.InstanceId).Order().ToArray());
        Assert.Single(allocations.Select(value => value.InstanceGroupId).Distinct());
        Assert.Equal(1, result.Statistics.ExpandedSegmentCount);
        Assert.Equal(1, result.Statistics.ParticipatingEventInstrumentCount);
        Assert.Equal(1, result.Statistics.ParticipatingSubVoiceCount);
        Assert.Equal(1, result.Statistics.UsedPortCount);
        Assert.Null(result.Statistics.ResourceShortage);
    }

    [Fact]
    public void IsolatedInstancesReuseOnlyNonoverlappingSegmentOwnedLanes()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 960);
        fixture.Instrument.RequiresChannelIsolation = true;
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        LogicalNote first = CompilerTestProject.AddNote(
            fixture.Segment,
            fixture.Instrument,
            0,
            240);
        LogicalNote adjacent = CompilerTestProject.AddNote(
            fixture.Segment,
            fixture.Instrument,
            240,
            240);
        LogicalNote overlapping = CompilerTestProject.AddNote(
            fixture.Segment,
            fixture.Instrument,
            300,
            240);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);
        ChannelUnitAllocation[] allocations = result.Allocations.ToArray();

        Assert.True(result.IsConsumable);
        Assert.Equal(2, result.Statistics.PeakChannelUnitCount);
        Assert.Equal(2, allocations.Select(value => value.InstanceGroupId).Distinct().Count());
        Assert.Equal(2, allocations.Select(value => (value.ZeroBasedPort, value.ZeroBasedChannel)).Distinct().Count());
        Assert.All(allocations, value => Assert.Equal(960, value.EndTick));
        Assert.Equal(
            allocations.Single(value => value.InstanceId == first.Id).InstanceGroupId,
            allocations.Single(value => value.InstanceId == adjacent.Id).InstanceGroupId);
        Assert.NotEqual(
            allocations.Single(value => value.InstanceId == adjacent.Id).InstanceGroupId,
            allocations.Single(value => value.InstanceId == overlapping.Id).InstanceGroupId);
    }

    [Fact]
    public void PortStatisticsCountOnlyActuallyUsedPorts()
    {
        var fixture = CompilerTestProject.Create(subVoiceCount: 17);
        foreach (SubVoice voice in fixture.Instrument.SubVoices)
        {
            voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        }
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable);
        Assert.Equal(17, result.Statistics.PeakChannelUnitCount);
        Assert.Equal(17, result.Statistics.ParticipatingSubVoiceCount);
        Assert.Equal(2, result.Statistics.UsedPortCount);
    }

    [Fact]
    public void ExplicitRangeIgnoresLaterResourceShortage()
    {
        MidoraProject project = CreateOverLimitProject(1_000);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(
            project,
            new CompilationRequest
            {
                Purpose = CompilationPurpose.Range,
                StartTick = 0,
                EndTick = 500
            });

        Assert.True(result.IsConsumable);
        Assert.Empty(result.Events.ToArray());
        Assert.Empty(result.Allocations.ToArray());
        Assert.Equal(0, result.Statistics.ExpandedInstanceCount);
        Assert.Equal(0, result.Statistics.PeakChannelUnitCount);
        Assert.Null(result.Statistics.ResourceShortage);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "MIDORA2202");
    }

    [Fact]
    public void ResourceShortageDetailsAreFullIncrementalEquivalent()
    {
        MidoraProject project = CreateOverLimitProject(0);
        using MidoraCompiler incrementalCompiler = new();

        CanonicalCompiledResult full = new MidoraCompiler().CompileFull(project);
        CanonicalCompiledResult incremental = incrementalCompiler.CompileIncremental(
            project,
            ProjectChangeSet.Everything);

        Assert.False(full.IsConsumable);
        Assert.False(incremental.IsConsumable);
        ResourceShortageDetails expected = Assert.IsType<ResourceShortageDetails>(
            full.Statistics.ResourceShortage);
        ResourceShortageDetails actual = Assert.IsType<ResourceShortageDetails>(
            incremental.Statistics.ResourceShortage);
        Assert.Equal(expected.Range, actual.Range);
        Assert.Equal(expected.RequestedChannelUnitCount, actual.RequestedChannelUnitCount);
        Assert.Equal(expected.AvailableChannelUnitCount, actual.AvailableChannelUnitCount);
        Assert.Equal(expected.TrackIds.ToArray(), actual.TrackIds.ToArray());
        Assert.Equal(expected.SegmentIds.ToArray(), actual.SegmentIds.ToArray());
        Assert.Equal(expected.LogicalNoteIds.ToArray(), actual.LogicalNoteIds.ToArray());
        Assert.Equal(expected.EventInstrumentIds.ToArray(), actual.EventInstrumentIds.ToArray());
        Assert.Equal(expected.SubVoiceIds.ToArray(), actual.SubVoiceIds.ToArray());
        Assert.Equal(full.Diagnostics, incremental.Diagnostics);
    }

    private static MidoraProject CreateOverLimitProject(long startTick)
    {
        var fixture = CompilerTestProject.Create(subVoiceCount: 256, segmentLength: 2_000);
        foreach (SubVoice voice in fixture.Instrument.SubVoices)
        {
            voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        }
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, startTick, 480);

        EventInstrument second = new(fixture.Project)
        {
            Name = "Second",
            TemplateLengthTicks = 480,
            OverlapPolicy = OverlapPolicy.Warn
        };
        SubVoice secondVoice = new(fixture.Project);
        secondVoice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        second.SubVoices.Add(secondVoice);
        fixture.Project.EventInstruments.Add(second);
        LogicalTrack secondTrack = new(fixture.Project)
        {
            Name = "Second",
            EventInstrumentId = second.Id
        };
        Segment secondSegment = new(fixture.Project) { LengthTicks = 2_000 };
        CompilerTestProject.RegisterSegment(fixture.Project, secondSegment);
        CompilerTestProject.AddNote(secondSegment, second, startTick, 480);
        secondTrack.Segments.Add(secondSegment);
        fixture.Project.Tracks.Add(secondTrack);
        return fixture.Project;
    }
}
