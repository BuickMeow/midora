using Midora.Domain;

namespace Midora.Compiler.Tests;

public sealed class CompilationContextSummaryTests
{
    [Fact]
    public void DefaultFullCompilationRecordsNaturalRangeAndAllSelection()
    {
        var fixture = CompilerTestProject.Create();

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);
        CompilationContextSummary context = result.Context;

        Assert.Same(context, result.Context);
        Assert.Equal(CompilationPurpose.FullProject, context.Purpose);
        Assert.Equal(0, context.StartTick);
        Assert.Null(context.RequestedEndTick);
        Assert.Equal(result.EndTick, context.EndTick);
        Assert.Equal(CompilationEndTickSource.NaturalContent, context.EndTickSource);
        Assert.True(context.IncludesAllTracks);
        Assert.True(context.IncludesAllSubVoices);
        Assert.True(context.IsFullProject);
        Assert.False(context.TreatWarningsAsErrors);
        Assert.Empty(context.IncludedTrackIds.ToArray());
        Assert.Empty(context.IncludedSubVoiceIds.ToArray());
    }

    [Fact]
    public void ExplicitSelectionsAreSortedAndFrozenIndependentlyFromMutableRequestSets()
    {
        var fixture = CompilerTestProject.Create();
        HashSet<MidoraId> tracks = [fixture.Track.Id];
        HashSet<MidoraId> subVoices = [fixture.Voice.Id];
        CompilationRequest request = new()
        {
            Purpose = CompilationPurpose.Playback,
            StartTick = 10,
            EndTick = 100,
            IncludedTrackIds = tracks,
            IncludedSubVoiceIds = subVoices,
            TreatWarningsAsErrors = true
        };

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project, request);
        tracks.Clear();
        subVoices.Clear();
        CompilationContextSummary context = result.Context;

        Assert.Equal(CompilationEndTickSource.ExplicitRequest, context.EndTickSource);
        Assert.Equal(100, context.RequestedEndTick);
        Assert.Equal(100, context.EndTick);
        Assert.False(context.IncludesAllTracks);
        Assert.False(context.IncludesAllSubVoices);
        Assert.Equal([fixture.Track.Id], context.IncludedTrackIds.ToArray());
        Assert.Equal([fixture.Voice.Id], context.IncludedSubVoiceIds.ToArray());
        Assert.True(context.TreatWarningsAsErrors);
        Assert.True(context.IsPlayback);
        Assert.False(context.IsFullProject);
    }

    [Fact]
    public void ProjectEndMarkerDefaultIsDistinguishedFromExplicitEnd()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Project.SetEndMarker(720);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.Equal(720, result.Context.EndTick);
        Assert.Null(result.Context.RequestedEndTick);
        Assert.Equal(CompilationEndTickSource.ProjectEndMarker, result.Context.EndTickSource);
        Assert.True(result.Context.UsesProjectEndMarkerAsDefault);
    }

    [Theory]
    [InlineData(CompilationPurpose.SegmentPreview, true, false, false)]
    [InlineData(CompilationPurpose.EventInstrumentPreview, true, false, false)]
    [InlineData(CompilationPurpose.MidiExport, false, true, false)]
    [InlineData(CompilationPurpose.AudioRender, false, false, true)]
    [InlineData(CompilationPurpose.LogicalTrackAudioRender, false, false, true)]
    public void PurposeSummaryExposesConsumerCategory(
        CompilationPurpose purpose,
        bool preview,
        bool midiExport,
        bool audioRender)
    {
        var fixture = CompilerTestProject.Create();

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest { Purpose = purpose });

        Assert.Equal(preview, result.Context.IsPreview);
        Assert.Equal(midiExport, result.Context.IsMidiExportPreparation);
        Assert.Equal(audioRender, result.Context.IsAudioRenderPreparation);
    }

    [Fact]
    public void FailedResultStillCarriesTheFrozenCompilationContext()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Segment.LengthTicks = 0;

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest
            {
                Purpose = CompilationPurpose.Range,
                StartTick = 20,
                EndTick = 30
            });

        Assert.False(result.IsConsumable);
        Assert.Equal(CompilationPurpose.Range, result.Context.Purpose);
        Assert.Equal(20, result.Context.StartTick);
        Assert.Equal(30, result.Context.EndTick);
        Assert.Equal(CompilationEndTickSource.ExplicitRequest, result.Context.EndTickSource);
    }
}
