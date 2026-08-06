using Midora.Domain;

namespace Midora.Compiler.Tests;

public sealed class DebugDiagnosticTests
{
    [Fact]
    public void DebugDiagnosticsAreOptInAndDoNotChangeCanonicalOutput()
    {
        var fixture = CreateAudibleFixture();
        using MidoraCompiler compiler = new();

        CanonicalCompiledResult standard = compiler.CompileFull(fixture.Project);
        CanonicalCompiledResult debug = compiler.CompileFull(
            fixture.Project,
            new CompilationRequest { CollectDebugDiagnostics = true });

        Assert.DoesNotContain(standard.Diagnostics, value =>
            value.Severity == DiagnosticSeverity.Debug);
        Assert.Equal(
            ["MIDORA2900", "MIDORA2901"],
            debug.Diagnostics
                .Where(value => value.Severity == DiagnosticSeverity.Debug)
                .Select(value => value.Code)
                .ToArray());
        Assert.Equal(standard.Fingerprint, debug.Fingerprint);
        Assert.Equal(standard.Events.ToArray(), debug.Events.ToArray());
        Assert.Equal(standard.Allocations.ToArray(), debug.Allocations.ToArray());
        Assert.Equal(standard.Statistics, debug.Statistics);
    }

    [Fact]
    public void FullAndIncrementalDebugDiagnosticsRemainFormallyEquivalent()
    {
        var fixture = CreateAudibleFixture();
        CompilationRequest request = new() { CollectDebugDiagnostics = true };
        using MidoraCompiler compiler = new();

        CanonicalCompiledResult full = compiler.CompileFull(fixture.Project, request);
        CanonicalCompiledResult incremental = compiler.CompileIncremental(
            fixture.Project,
            new ProjectChangeSet(),
            request);

        Assert.Equal(full.Diagnostics, incremental.Diagnostics);
        Assert.Equal(full.Fingerprint, incremental.Fingerprint);
        Assert.Equal(full.Events.ToArray(), incremental.Events.ToArray());
        Assert.True(incremental.Context.CollectDebugDiagnostics);
    }

    [Fact]
    public void DebugDiagnosticsAreReturnedForSemanticFailureWithoutChangingFailureStage()
    {
        var fixture = CompilerTestProject.Create();

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest
            {
                StartTick = -1,
                CollectDebugDiagnostics = true
            });

        Assert.False(result.IsConsumable);
        Assert.Equal(CompilationFailureStage.SemanticValidation, result.FailureStage);
        Assert.Equal(2, result.Diagnostics.Count(value =>
            value.Severity == DiagnosticSeverity.Debug));
        Assert.Contains(result.Diagnostics, value =>
            value.Code == "MIDORA2901"
            && value.Message.Contains("partial/SemanticValidation", StringComparison.Ordinal));
    }

    [Fact]
    public void DebugSeverityNeverTriggersWarningFailurePolicy()
    {
        var fixture = CreateAudibleFixture();

        CanonicalCompiledResult result = new MidoraCompiler().CompileFull(
            fixture.Project,
            new CompilationRequest
            {
                TreatWarningsAsErrors = true,
                CollectDebugDiagnostics = true
            });

        Assert.True(result.IsConsumable);
        Assert.Null(result.FailureStage);
        Assert.Contains(result.Diagnostics, value =>
            value.Severity == DiagnosticSeverity.Debug);
    }

    private static (MidoraProject Project, LogicalTrack Track, Segment Segment, EventInstrument Instrument, SubVoice Voice)
        CreateAudibleFixture()
    {
        var fixture = CompilerTestProject.Create();
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
        _ = CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);
        return fixture;
    }
}
