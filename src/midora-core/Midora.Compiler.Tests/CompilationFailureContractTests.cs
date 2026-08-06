using Midora.Domain;

namespace Midora.Compiler.Tests;

public sealed class CompilationFailureContractTests
{
    [Fact]
    public void SemanticFailureReturnsExplicitNonConsumablePartialResult()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Segment.LengthTicks = 0;

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.False(result.IsConsumable);
        Assert.True(result.IsPartial);
        Assert.Equal(CompilationFailureStage.SemanticValidation, result.FailureStage);
        Assert.Equal(1, result.Statistics.SourceTrackCount);
        Assert.Empty(result.Events.ToArray());
    }

    [Fact]
    public void InvalidCompilationPurposeFailsDuringSemanticValidation()
    {
        var fixture = CompilerTestProject.Create();

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest { Purpose = (CompilationPurpose)999 });

        Assert.False(result.IsConsumable);
        Assert.True(result.IsPartial);
        Assert.Equal(CompilationFailureStage.SemanticValidation, result.FailureStage);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "MIDORA1004");
    }

    [Fact]
    public void UnknownIncludedSubVoiceFailsInsteadOfProducingSilentEmptyOutput()
    {
        var fixture = CompilerTestProject.Create();

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest
            {
                Purpose = CompilationPurpose.EventInstrumentPreview,
                IncludedSubVoiceIds = [MidoraId.FromSequence(fixture.Project.NextStableId + 100)]
            });

        Assert.False(result.IsConsumable);
        Assert.True(result.IsPartial);
        Assert.Equal(CompilationFailureStage.SemanticValidation, result.FailureStage);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "MIDORA1005");
    }

    [Fact]
    public void ExpandedMidiValueFailureReportsInstanceExpansionStage()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 127, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240, 127);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.False(result.IsConsumable);
        Assert.True(result.IsPartial);
        Assert.Equal(CompilationFailureStage.InstanceExpansion, result.FailureStage);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "MIDORA2101");
        Assert.Equal(1, result.Statistics.ExpandedInstanceCount);
    }

    [Fact]
    public void RejectedOverlapReportsOverlapValidationStage()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 960);
        fixture.Instrument.OverlapPolicy = OverlapPolicy.Reject;
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 120, 240);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.False(result.IsConsumable);
        Assert.True(result.IsPartial);
        Assert.Equal(CompilationFailureStage.OverlapValidation, result.FailureStage);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "MIDORA2201"
            && diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Equal(2, result.Statistics.ExpandedInstanceCount);
    }

    [Fact]
    public void WarningPolicyFailureKeepsWarningSeverityAndReportsPolicyStage()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 960);
        fixture.Instrument.OverlapPolicy = OverlapPolicy.Warn;
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 120, 240);

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest { TreatWarningsAsErrors = true });

        Assert.False(result.IsConsumable);
        Assert.True(result.IsPartial);
        Assert.Equal(CompilationFailureStage.WarningPolicy, result.FailureStage);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "MIDORA2201"
            && diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void SuccessfulCompilationHasNoFailureStage()
    {
        var fixture = CompilerTestProject.Create();

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(fixture.Project);

        Assert.True(result.IsConsumable);
        Assert.False(result.IsPartial);
        Assert.Null(result.FailureStage);
    }
}
